using System.Collections;
using System.Reflection;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;
using Klangbruecke.Tests.Diagnostics;
using Klangbruecke.Tests.Fakes;
using Microsoft.Win32;
using Xunit;

namespace Klangbruecke.Tests.Platform;

/// <summary>
/// <see cref="PowerNotifier"/> without suspending the machine.
///
/// Two halves are reachable, and they need different instruments.
///
/// The <b>filter</b> - raise <c>Resumed</c> for <see cref="PowerModes.Resume"/> and for nothing else -
/// is reached through <see cref="PowerNotifier.OnPowerModeChanged"/>, which is public for exactly this
/// reason (same precedent as <c>LinkMonitor.OnCandidateAdded</c>). <c>PowerModeChangedEventArgs</c> has
/// a public constructor, so all three <see cref="PowerModes"/> values can be delivered by hand.
///
/// The <b>subscription</b> cannot be reached that way: <c>SystemEvents.PowerModeChanged</c> is a static
/// event with no public way to read its handler list, and a real suspend is the only thing that raises
/// it. So <see cref="PowerModeHandlerCount"/> reads the list by reflection. That probe is deliberately
/// strict - it throws rather than answering zero when a field it needs is missing - because a probe
/// that quietly answers zero would make <see cref="Dispose_unsubscribes_from_SystemEvents"/> pass
/// whether or not the unsubscribe is there, which is worse than having no test at all.
/// <see cref="Start_subscribes_to_SystemEvents"/> is what pins it honest from the other side: it
/// demands a count of <b>one</b>, so a probe looking in the wrong place fails there.
///
/// These tests really do subscribe to the process-global <c>SystemEvents</c>, which starts its hidden
/// message window on first use. That is why the counts are per-instance rather than totals, and it is
/// another reason <c>AssemblyInfo.cs</c> disables parallelisation.
/// </summary>
public sealed class PowerNotifierTests : IDisposable
{
    private readonly ILog _original = Log.Current;
    private readonly RecordingLog _log = new();

    public PowerNotifierTests() => Log.Current = _log;

    public void Dispose() => Log.Current = _original;

    // --- the brief's five ---

    [Fact]
    public void Dispose_without_Start_does_not_throw()
    {
        var notifier = new PowerNotifier();

        // TrayContext unwinds blind: it disposes whatever it built, without knowing how far startup
        // got. A notifier constructed and never started has nothing to unsubscribe, and having to
        // guard that at the call site is how teardown paths grow the conditionals that then get one
        // case wrong.
        Assert.Null(Record.Exception(notifier.Dispose));
        Assert.Equal(0, PowerModeHandlerCount(notifier));
    }

    // Read what this does and does not pin. It does catch a Dispose that never unsubscribes - the
    // count assertion below reddens for that, measured. What it cannot catch is the removal of
    // Dispose's own `if (_disposed) return;` guard: the second pass finds _subscribed already false
    // and does nothing, and an unguarded second `-=` for a handler no longer registered is a no-op in
    // SystemEvents too, so idempotence here is structural rather than asserted. Same situation, and
    // the same labelling, as LinkMonitorTests.Dispose_is_idempotent. The guard becomes load-bearing,
    // and this test becomes able to fail on it, the moment Dispose does something not already
    // idempotent on its own - and whoever makes that change owns the assertion for it.
    [Fact]
    public void Dispose_is_idempotent()
    {
        var notifier = new PowerNotifier();
        notifier.Start();
        notifier.Dispose();

        Assert.Null(Record.Exception(notifier.Dispose));
        Assert.Equal(0, PowerModeHandlerCount(notifier));
    }

    [Fact]
    public void Dispose_unsubscribes_from_SystemEvents()
    {
        // The one real hazard in this class. SystemEvents holds the handler in a plain field - not a
        // WeakReference, confirmed by reading the list below - so a notifier that never unsubscribes
        // is rooted for the life of the process and keeps being handed resumes long after the app
        // believes it is gone.
        var first = new PowerNotifier();
        first.Start();
        Assert.Equal(1, PowerModeHandlerCount(first));

        first.Dispose();
        Assert.Equal(0, PowerModeHandlerCount(first));

        // The brief's second half: a second instance over the same static event subscribes and
        // unsubscribes cleanly, and the first one's handler does not come back. This is what says the
        // leak is per-instance and actually released rather than merely masked by the next
        // subscription overwriting it.
        var second = new PowerNotifier();
        second.Start();

        Assert.Equal(1, PowerModeHandlerCount(second));
        Assert.Equal(0, PowerModeHandlerCount(first));

        second.Dispose();

        Assert.Equal(0, PowerModeHandlerCount(second));
        Assert.Equal(0, PowerModeHandlerCount(first));
    }

    [Fact]
    public void FakePowerNotifier_raises_Resumed_on_demand()
    {
        var notifier = new FakePowerNotifier();
        int resumes = 0;
        notifier.Resumed += (_, _) => resumes++;

        notifier.RaiseResumed();
        notifier.RaiseResumed();

        // Twice, because the double must be able to express a second resume - a lid closed and opened
        // again inside one session is the ordinary case, not an edge one.
        Assert.Equal(2, resumes);
    }

    [Fact]
    public void FakePowerNotifier_records_Start()
    {
        var notifier = new FakePowerNotifier();

        Assert.False(notifier.Started);

        notifier.Start();

        Assert.True(notifier.Started);
    }

    // --- beyond the brief ---

    [Fact]
    public void FakePowerNotifier_records_Dispose()
    {
        var notifier = new FakePowerNotifier();

        Assert.False(notifier.Disposed);

        notifier.Dispose();

        // Consumers need this: the real notifier leaks a static subscription if it is not disposed,
        // so "did ConnectionManager let go of it?" is a question its teardown test has to be able to
        // ask.
        Assert.True(notifier.Disposed);
    }

    [Fact]
    public void A_resume_raises_Resumed()
    {
        using var notifier = new PowerNotifier();
        int resumes = 0;
        object? sender = null;
        notifier.Resumed += (s, _) =>
        {
            resumes++;
            sender = s;
        };

        notifier.OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Resume));

        Assert.Equal(1, resumes);

        // The notifier, not whatever SystemEvents passed. Subscribers identify the source they
        // registered with; forwarding a foreign sender is how a consumer holding two seams ends up
        // acting on the wrong one.
        Assert.Same(notifier, sender);
    }

    [Theory]
    [InlineData(PowerModes.Suspend)]
    [InlineData(PowerModes.StatusChange)]
    public void A_notification_that_is_not_a_resume_does_not_raise_Resumed(PowerModes mode)
    {
        using var notifier = new PowerNotifier();
        int resumes = 0;
        notifier.Resumed += (_, _) => resumes++;

        notifier.OnPowerModeChanged(this, new PowerModeChangedEventArgs(mode));

        // StatusChange is the noisy one - it fires on every AC/battery transition, which on a laptop
        // is often - and Suspend is the actively wrong one: acting on it would schedule a settle and a
        // reconcile against a machine that is on its way down.
        Assert.Equal(0, resumes);
    }

    [Fact]
    public void A_resume_is_logged()
    {
        using var notifier = new PowerNotifier();

        notifier.OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Resume));

        // Reconnect after sleep is one of the two paths CLAUDE.md calls historically fragile, and the
        // log is the only instrument for it after the fact. Without a line here, a log that shows no
        // reconnect cannot distinguish "the resume never arrived" from "it arrived and nothing acted
        // on it" - two different bugs in two different components.
        (LogLevel Level, string Message, Exception? Exception) entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Contains("resume", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PowerModes.Suspend)]
    [InlineData(PowerModes.StatusChange)]
    public void A_notification_that_is_not_a_resume_is_not_logged(PowerModes mode)
    {
        using var notifier = new PowerNotifier();

        notifier.OnPowerModeChanged(this, new PowerModeChangedEventArgs(mode));

        // StatusChange fires on every power-source change. Logging it would put a line in the file for
        // an event this app does nothing about, and the file is read by a human looking for one thing.
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Start_subscribes_to_SystemEvents()
    {
        using var notifier = new PowerNotifier();

        Assert.Equal(0, PowerModeHandlerCount(notifier));

        notifier.Start();

        // Also the probe's own honesty check: a reflection probe that looked in the wrong place, or
        // matched the wrong owner, would answer zero here and fail.
        Assert.Equal(1, PowerModeHandlerCount(notifier));
    }

    [Fact]
    public void Start_twice_subscribes_only_once()
    {
        using var notifier = new PowerNotifier();

        notifier.Start();
        notifier.Start();

        // Not tidiness. SystemEvents.RemoveEventHandler removes one entry per call, so a double
        // subscribe cannot be undone by a single Dispose - the second registration would outlive the
        // app. It would also raise Resumed twice per wake, which downstream turns into two settles and
        // two forced reconciles.
        Assert.Equal(1, PowerModeHandlerCount(notifier));

        notifier.Dispose();

        Assert.Equal(0, PowerModeHandlerCount(notifier));
    }

    [Fact]
    public void Start_after_Dispose_throws_rather_than_leaking_a_subscription_nothing_will_remove()
    {
        var notifier = new PowerNotifier();
        notifier.Dispose();

        // Same reasoning as LinkMonitor.Watch, and it bites harder here. Dispose's idempotence guard
        // returns early on a second call, so a subscription taken after the first Dispose would never
        // be removed - and unlike a DeviceWatcher, this one is rooted by a static event, so nothing
        // can ever collect it. Refused rather than tolerated: the caller doing it has a real defect
        // and nothing downstream can detect it.
        Assert.Throws<ObjectDisposedException>(notifier.Start);
        Assert.Equal(0, PowerModeHandlerCount(notifier));
    }

    // --- the probe -------------------------------------------------------------------------------

    /// <summary>
    /// How many handlers belonging to <paramref name="owner"/> are registered on
    /// <see cref="SystemEvents.PowerModeChanged"/> right now.
    ///
    /// Reflection over BCL internals, which is not free and is not something to reach for twice. It is
    /// justified here because the alternative was a test that could not fail: the event is static, has
    /// no public reader, and is raised only by a real machine suspend. The shape it reads was measured
    /// on this machine against .NET 8 - <c>s_handlers</c> is a
    /// <c>Dictionary&lt;object, List&lt;SystemEventInvokeInfo&gt;&gt;</c> keyed by the private
    /// <c>s_onPowerModeChangedEvent</c> sentinel, and each entry holds a plain
    /// <c>Delegate _delegate</c> (a strong reference, which is the leak this whole test exists for).
    ///
    /// Every lookup that cannot be satisfied throws. A future .NET that renames any of these fields
    /// must make this fail loudly, because the failure mode of the alternative is a permanently green
    /// test asserting nothing.
    ///
    /// Counts by owner rather than in total, and that is not fastidiousness. A test that fails partway
    /// through leaves its own notifier undisposed - and therefore subscribed - for the rest of the
    /// run, so a total would turn one red test into a cascade of them in whatever order xunit happened
    /// to pick. Measured, while mutating this very method. Other components in the process may hold
    /// their own SystemEvents subscriptions too.
    /// </summary>
    private static int PowerModeHandlerCount(object owner)
    {
        const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;

        Type type = typeof(SystemEvents);

        FieldInfo keyField = type.GetField("s_onPowerModeChangedEvent", Statics)
            ?? throw new InvalidOperationException(
                "SystemEvents.s_onPowerModeChangedEvent is gone - the handler probe cannot see the "
                + "PowerModeChanged subscription any more and must not be trusted.");

        FieldInfo handlersField = type.GetField("s_handlers", Statics)
            ?? throw new InvalidOperationException(
                "SystemEvents.s_handlers is gone - the handler probe cannot see any subscription any "
                + "more and must not be trusted.");

        object key = keyField.GetValue(null)
            ?? throw new InvalidOperationException("SystemEvents.s_onPowerModeChangedEvent is null.");

        // Null until something in the process subscribes to any SystemEvents event, and the key is
        // absent until something subscribes to this one. Both are honest zeroes, not lookup failures.
        if (handlersField.GetValue(null) is not IDictionary handlers)
        {
            return 0;
        }

        if (handlers[key] is not IEnumerable registered)
        {
            return 0;
        }

        int count = 0;
        foreach (object? info in registered)
        {
            if (info is null)
            {
                continue;
            }

            FieldInfo delegateField =
                info.GetType().GetField("_delegate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    $"{info.GetType().Name} no longer has a _delegate field - the handler probe "
                    + "cannot tell whose handler is registered and must not be trusted.");

            if (delegateField.GetValue(info) is Delegate handler && ReferenceEquals(handler.Target, owner))
            {
                count++;
            }
        }

        return count;
    }
}
