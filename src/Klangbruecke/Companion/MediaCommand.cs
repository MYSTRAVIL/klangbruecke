namespace Klangbruecke.Companion;

/// <summary>
/// The four transport actions the PC can ask the phone to perform. Phase 2 is deliberately this
/// short list - no seek, no volume, no shuffle - because these four are the ones a media key or an
/// SMTC button raises, and each maps one-to-one onto a phone-side <c>transportControls</c> call.
/// </summary>
internal enum MediaCommand
{
    Play,
    Pause,
    Next,
    Previous,
}
