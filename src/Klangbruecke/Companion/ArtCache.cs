namespace Klangbruecke.Companion;

/// <summary>
/// Album art keyed by the phone's opaque per-track hash. Insertion-ordered and bounded: only the
/// current track's art is ever shown, so a small cap keeps memory flat and evicts the oldest first when
/// exceeded. Fetching art once per track (a cache miss sends one RequestArt) is the whole point - the
/// phone never re-sends a JPEG the PC already holds.
///
/// Not thread-safe by design: it lives on the single-threaded <see cref="CompanionLink"/> like
/// everything else in <c>Companion/</c>, so it holds no lock.
/// </summary>
internal sealed class ArtCache
{
    private readonly int _capacity;
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, byte[]> _bytes = new();

    public ArtCache(int capacity = 4) => _capacity = capacity < 1 ? 1 : capacity;

    public bool TryGet(string hash, out byte[] jpeg) => _bytes.TryGetValue(hash, out jpeg!);

    public void Put(string hash, byte[] jpeg)
    {
        if (_bytes.ContainsKey(hash))
        {
            // Same hash seen again: refresh the bytes but leave its age alone.
            _bytes[hash] = jpeg;
            return;
        }

        _bytes[hash] = jpeg;
        _order.AddLast(hash);

        while (_order.Count > _capacity)
        {
            string oldest = _order.First!.Value;
            _order.RemoveFirst();
            _bytes.Remove(oldest);
        }
    }
}
