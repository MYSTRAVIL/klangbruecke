namespace Klangbruecke.Companion;

/// <summary>
/// One PlaybackState frame decoded: whether the phone is playing, plus the timeline data the PC needs
/// to drive an advancing seek bar. <see cref="PositionMs"/> is <em>live as of send</em> - the phone
/// interpolated its framework position up to the moment it sent the frame - so the PC re-bases its own
/// clock from it and advances locally by <c>elapsed x speed</c>. That is what lets the bar move while
/// the phone stays silent between play/pause/seek (the Android power constraint).
/// </summary>
internal sealed record PlaybackUpdate(bool IsPlaying, long PositionMs, long TimestampMs, double Speed);
