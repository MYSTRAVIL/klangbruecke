using Klangbruecke.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Klangbruecke.Companion;

/// <summary>
/// The RFCOMM half of the phone-media remote, below the <see cref="ICompanionTransport"/> seam: the
/// uncached SDP discovery, the <c>StreamSocket</c>, and the read loop that reassembles the framed
/// stream. Confirmed working end-to-end in FINDINGS 20.2 / 20.3 - this class ports that spike.
///
/// Two things this class must get right that the spike could gloss over:
/// <list type="bullet">
/// <item><b>Discovery is uncached.</b> Windows caches a bonded device's SDP records at pair time and
/// does not refresh them when the phone advertises a service afterwards, so the cached
/// <c>DeviceInformation</c> selector never sees the companion service. Each paired classic-BT device
/// is asked with <see cref="BluetoothCacheMode.Uncached"/> instead, which forces a live SDP inquiry
/// and finds the service without re-pairing (re-pairing would disturb the audio and risk the stale-IRK
/// bug in FINDINGS 3).</item>
/// <item><b>The read loop never throws out.</b> It runs on a background task; a socket that drops
/// mid-read is ordinary, not exceptional, so every path out of the loop is caught and logged and ends
/// in exactly one <see cref="Disconnected"/> - the orchestrator's cue to clear the surface and back
/// off. A handler that throws is logged and swallowed so it can never be mistaken for a dropped
/// socket.</item>
/// </list>
///
/// The byte asymmetry the seam documents lives here: <see cref="SendAsync"/> writes a whole frame the
/// caller already length-prefixed, while <see cref="FrameReceived"/> hands back a single reassembled
/// frame's <c>type + payload</c> with the length stripped, because this class is the only thing that
/// had to carve frames out of a stream.
/// </summary>
internal sealed class RfcommCompanionTransport : ICompanionTransport
{
    /// <summary>
    /// The companion service's SDP UUID, defined once. Identical to the Android side's advertised
    /// record and to the Phase-1 spike; a mismatch here makes discovery silently find nothing.
    /// </summary>
    internal static readonly Guid ServiceUuid = new("6f5e4d3c-2b1a-4c8d-9e7f-0a1b2c3d4e5f");

    // Partial reads: LoadAsync returns as soon as any bytes are available (up to this many) rather
    // than blocking for a full buffer, which is what lets the loop drain frames as they arrive. The
    // size is a ceiling, not a promise - a single read may return one byte.
    private const uint ReadChunkSize = 4096;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private StreamSocket? _socket;
    private DataReader? _reader;
    private DataWriter? _writer;
    private RfcommDeviceService? _service;
    private BluetoothDevice? _device;
    private Task? _readLoop;

    // Set true while a connection is being torn down on purpose (a fresh connect, or Dispose), so the
    // read loop that the teardown breaks does not report the deliberate close as a dropped link.
    private volatile bool _closing;
    private volatile bool _disposed;

    public bool IsConnected { get; private set; }

    public event EventHandler<byte[]>? FrameReceived;

    public event EventHandler? Disconnected;

    public async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            return false;
        }

        // Drop any stale connection state from a previous session before building a new one. The
        // previous read loop has already ended (that is what raised Disconnected and brought the
        // orchestrator back here), so this only disposes objects, it does not race a live loop.
        CloseCurrentConnection();

        try
        {
            RfcommDeviceService? service = await DiscoverServiceAsync(ct).ConfigureAwait(false);
            if (service is null)
            {
                Log.Info("No paired device advertises the companion service (uncached SDP found nothing).");
                return false;
            }

            var socket = new StreamSocket();
            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName)
                .AsTask(ct).ConfigureAwait(false);

            _service = service;
            _socket = socket;
            _writer = new DataWriter(socket.OutputStream);
            _reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
            _closing = false;
            IsConnected = true;

            Log.Info($"Companion RFCOMM connected to {service.ConnectionHostName}:{service.ConnectionServiceName}.");

            _readLoop = Task.Run(() => ReadLoopAsync(_reader, _cts.Token));
            return true;
        }
        catch (OperationCanceledException)
        {
            CloseCurrentConnection();
            return false;
        }
        catch (Exception ex)
        {
            // Any failure is a false return the orchestrator backs off on, never a throw. Discovery
            // and connect both reach the radio, and either can fault for reasons - phone asleep, no
            // service yet - that are transient and not this app's to fix.
            Log.Warn($"Companion RFCOMM connect failed: {ex.Message}");
            CloseCurrentConnection();
            return false;
        }
    }

    /// <summary>
    /// Enumerates paired classic-BT devices and asks each, uncached, for the companion service.
    /// Returns the first match, keeping its owning <see cref="BluetoothDevice"/> alive in a field for
    /// the connection's lifetime; non-matching devices are disposed as we go.
    /// </summary>
    private async Task<RfcommDeviceService?> DiscoverServiceAsync(CancellationToken ct)
    {
        string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        DeviceInformationCollection paired =
            await DeviceInformation.FindAllAsync(selector).AsTask(ct).ConfigureAwait(false);

        var serviceId = RfcommServiceId.FromUuid(ServiceUuid);

        foreach (DeviceInformation info in paired)
        {
            ct.ThrowIfCancellationRequested();

            BluetoothDevice? bt = null;
            try
            {
                bt = await BluetoothDevice.FromIdAsync(info.Id).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Not a classic-BT device, or the id no longer resolves. Skip it.
                Log.Info($"Companion discovery skipped '{info.Name}': {ex.Message}");
            }

            if (bt is null)
            {
                continue;
            }

            try
            {
                RfcommDeviceServicesResult result = await bt
                    .GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Uncached)
                    .AsTask(ct).ConfigureAwait(false);

                if (result.Services.Count > 0)
                {
                    _device = bt;
                    return result.Services[0];
                }
            }
            catch (Exception ex)
            {
                Log.Info($"Companion uncached SDP query on '{bt.Name}' failed: {ex.Message}");
            }

            bt.Dispose();
        }

        return null;
    }

    public async Task SendAsync(byte[] frame, CancellationToken ct)
    {
        DataWriter? writer = _writer;
        if (writer is null || !IsConnected)
        {
            throw new InvalidOperationException("The companion transport is not connected.");
        }

        // Serialize writes: the orchestrator sends commands fire-and-forget, so two StoreAsync calls
        // could otherwise overlap on the one DataWriter, which is not built for concurrent use.
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The frame already carries its length prefix (MediaProtocol framed it); write it as-is.
            writer.WriteBytes(frame);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(DataReader reader, CancellationToken ct)
    {
        var accumulator = new List<byte>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                uint loaded = await reader.LoadAsync(ReadChunkSize).AsTask(ct).ConfigureAwait(false);
                if (loaded == 0)
                {
                    // The peer closed the stream cleanly. An EOF is a drop like any other.
                    break;
                }

                var chunk = new byte[loaded];
                reader.ReadBytes(chunk);
                accumulator.AddRange(chunk);

                DrainFrames(accumulator);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed or reconnecting: a deliberate cancellation is not a link drop.
            return;
        }
        catch (Exception ex)
        {
            if (!_disposed && !_closing)
            {
                Log.Warn($"Companion RFCOMM read loop ended: {ex.Message}");
            }
        }
        finally
        {
            if (!_disposed && !_closing)
            {
                IsConnected = false;
                RaiseDisconnected();
            }
        }
    }

    /// <summary>
    /// Pulls every complete frame out of the accumulation buffer and raises one
    /// <see cref="FrameReceived"/> per frame with its <c>type + payload</c> (length prefix stripped),
    /// then removes the consumed bytes and leaves any partial tail for the next read.
    /// </summary>
    private void DrainFrames(List<byte> accumulator)
    {
        byte[] snapshot = accumulator.ToArray();
        ReadOnlySpan<byte> window = snapshot;

        while (MediaProtocol.TryReadFrame(ref window, out MessageType type, out ReadOnlyMemory<byte> payload))
        {
            RaiseFrame(type, payload);
        }

        int consumed = snapshot.Length - window.Length;
        if (consumed > 0)
        {
            accumulator.RemoveRange(0, consumed);
        }
    }

    private void RaiseFrame(MessageType type, ReadOnlyMemory<byte> payload)
    {
        // The seam hands back type + payload with no length prefix - exactly what CompanionLink reads
        // (frame[0] is the type, frame[1..] the payload).
        var frame = new byte[1 + payload.Length];
        frame[0] = (byte)type;
        payload.Span.CopyTo(frame.AsSpan(1));

        try
        {
            FrameReceived?.Invoke(this, frame);
        }
        catch (Exception ex)
        {
            // A throwing handler is not a socket failure; log it and keep draining so it can never end
            // the read loop and be mistaken for a dropped link.
            Log.Error("A companion FrameReceived handler threw.", ex);
        }
    }

    private void RaiseDisconnected()
    {
        try
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("A companion Disconnected handler threw.", ex);
        }
    }

    /// <summary>
    /// Tears the current socket, streams, service and device down. Silent by design: it runs on the
    /// hot reconnect path where a broken object failing to dispose is expected, and on Dispose where a
    /// log call must never be the thing that throws.
    /// </summary>
    private void CloseCurrentConnection()
    {
        _closing = true;
        IsConnected = false;

        try { _reader?.Dispose(); } catch { /* stream may already be broken */ }
        try { _writer?.Dispose(); } catch { /* stream may already be broken */ }
        try { _socket?.Dispose(); } catch { /* socket may already be closed */ }
        try { _service?.Dispose(); } catch { /* service handle may already be gone */ }
        try { _device?.Dispose(); } catch { /* device handle may already be gone */ }

        _reader = null;
        _writer = null;
        _socket = null;
        _service = null;
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancel first so an in-flight LoadAsync unwinds as a cancellation rather than faulting on the
        // stream we are about to dispose out from under it.
        try { _cts.Cancel(); } catch { /* already disposed */ }

        CloseCurrentConnection();

        try { _cts.Dispose(); } catch { /* already disposed */ }
        _writeGate.Dispose();
    }
}
