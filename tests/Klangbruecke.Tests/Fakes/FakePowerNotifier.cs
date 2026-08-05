using Klangbruecke.Platform;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// A resume that arrives because a test said so, not because the machine woke up.
///
/// This is the whole reason <see cref="IPowerNotifier"/> exists as an interface: the state machine's
/// behaviour across sleep/resume - a 5 s settle and then a forced reconcile - is one of the two paths
/// CLAUDE.md names as historically fragile, and suspending the dev machine is not something a suite
/// that runs in two seconds can do.
///
/// Deliberately dumb. It does not check <see cref="Started"/> before raising, does not refuse to raise
/// after <see cref="Dispose"/>, and holds no state beyond the two flags. A double that enforced rules
/// the real one does not would make its consumers' tests certify a contract nothing implements.
///
/// Public, in Fakes, and not <c>file</c>-scoped: <c>ConnectionManager</c>'s tests consume it too.
/// </summary>
public sealed class FakePowerNotifier : IPowerNotifier
{
    public event EventHandler? Resumed;

    /// <summary><see cref="Start"/> has been called at least once.</summary>
    public bool Started { get; private set; }

    /// <summary>
    /// <see cref="Dispose"/> has been called at least once. Here so a consumer's teardown test can
    /// assert the notifier was let go of - the real one holds a static subscription, so a consumer
    /// that forgets keeps receiving resumes for the life of the process.
    /// </summary>
    public bool Disposed { get; private set; }

    public void Start() => Started = true;

    /// <summary>The machine woke up.</summary>
    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Disposed = true;
}
