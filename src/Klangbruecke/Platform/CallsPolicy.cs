using Klangbruecke.Diagnostics;

namespace Klangbruecke.Platform;

public enum CallsAvailability
{
    Enabled,
    DisabledBySetting,
    DisabledNoPackageIdentity,
}

/// <summary>
/// Whether the calls half runs, and how far down its path a verdict lets a run go.
///
/// Deciding and acting on the decision live together on purpose. They were briefly split across a
/// separate CallTransportPlan type in another namespace, which put three types across two namespaces
/// between "is this enabled" and "so what do I do" - twice the surface of the music half's
/// AudioSinkPolicy for the same job.
/// </summary>
public static class CallsPolicy
{
    /// <summary>
    /// A half that is switched off or structurally unavailable is not a failure and must not be
    /// retried. The user's setting takes precedence: it is the more useful thing to report.
    /// </summary>
    public static CallsAvailability Decide(bool enableCalls, bool isPackaged)
    {
        if (!enableCalls)
        {
            return CallsAvailability.DisabledBySetting;
        }

        return isPackaged ? CallsAvailability.Enabled : CallsAvailability.DisabledNoPackageIdentity;
    }

    /// <summary>
    /// Why, in the words the tray and the log will carry. Total by construction - this feeds a status
    /// string, and neither caller can take a throw - so an unrecognised value gets a bare answer
    /// rather than inheriting whichever explanation the default arm happens to hold.
    /// </summary>
    public static string Explain(CallsAvailability availability) => availability switch
    {
        CallsAvailability.Enabled => "Calls enabled.",
        CallsAvailability.DisabledBySetting => "Calls disabled in settings.",

        // Names registration deliberately. Discovery works unpackaged - GetDeviceSelector,
        // FindAllAsync, FromId and IsRegistered() were all exercised against the real phone with no
        // identity - so "the capability does not apply" on its own would read as "enumeration is
        // pointless" and send the next maintainer after a problem they do not have.
        CallsAvailability.DisabledNoPackageIdentity =>
            "Calls disabled: no MSIX package identity. Discovering the phone's transport works "
            + "unpackaged; it is RegisterApp, claiming the hands-free role, that needs the restricted "
            + "phoneLineTransportManagement capability. Run the packaged build to route calls.",

        _ => "Calls disabled.",
    };

    /// <summary>
    /// What a verdict is worth in the log. One rule, shared with the music half's
    /// <c>AudioSinkPolicy.LevelFor</c>: a half the user switched off is Info, a half that cannot run
    /// at all is Warn.
    ///
    /// The two gates used to disagree - music gated off warned, calls gated off informed - so
    /// someone grepping [WRN] saw exactly half the story about the same root cause. Splitting on the
    /// *reason* rather than picking one level for both is what makes the rule mean something: the
    /// spec's "Disabled is not failed" is true of a setting the user chose, and is not true of a
    /// capability that cannot apply, which is the user asking for something the app cannot deliver.
    /// </summary>
    public static LogLevel LevelFor(CallsAvailability availability) => availability switch
    {
        CallsAvailability.Enabled => LogLevel.Info,
        CallsAvailability.DisabledBySetting => LogLevel.Info,

        // Default arm, so a reason added later is loud rather than quietly filed as ordinary
        // progress. Same instinct as Explain's default arm and ShouldRegister's.
        _ => LogLevel.Warn,
    };

    /// <summary>
    /// <b><see cref="ShouldRegister"/> is now wired; this one is not.</b> Read the next two paragraphs
    /// before reading either as a description of what the app does.
    ///
    /// <see cref="ShouldRegister"/> is consulted by <c>ConnectionManager.ApplySettingsToHalves</c>,
    /// which hands its verdict to <c>CallsHalf.Configure</c> as the half's enabled bit. Unpackaged,
    /// where it is false, the calls half reports <c>Enabled == false</c> and attempts nothing - not the
    /// <c>RegisterApp</c> that used to fail, back off, and retry at the 60 s ceiling for the life of the
    /// process while nothing consulted this. That was the fix this pair existed to state, and it is
    /// applied. <see cref="MenuItem"/> still tells the <em>user</em> why; the gate itself is the
    /// manager's.
    ///
    /// <see cref="ShouldEnumerate"/> is the more granular rule that was <em>not</em> adopted. It says
    /// an unpackaged run could still enumerate and log the transport - discovery works with no package
    /// identity (GetDeviceSelector, FindAllAsync, FromId, IsRegistered were all exercised against the
    /// real phone), and only RegisterApp needs the restricted capability. But <c>CallsHalf</c> couples
    /// enumerate and register in one flow, and the design gates the half as a whole
    /// (<c>Enabled iff Decide == Enabled</c>), so unpackaged it now does neither. It is kept, with its
    /// tests, as the documented alternative: wiring it would mean splitting enumerate from register in
    /// <c>CallsHalf</c> and giving the half a terminal "enumerated, cannot register" state instead of
    /// the Backoff it no longer reaches.
    ///
    /// <hr/>
    ///
    /// Enumerate unless the user turned calls off, in which case looking is pure noise.
    /// </summary>
    public static bool ShouldEnumerate(CallsAvailability availability) =>
        availability != CallsAvailability.DisabledBySetting;

    /// <summary>
    /// Register and connect only when nothing structural is in the way. Consulted by
    /// <c>ConnectionManager.ApplySettingsToHalves</c>, which passes the result to
    /// <c>CallsHalf.Configure</c> as the half's enabled bit - so unpackaged, where this is false, the
    /// half never attempts registration. See the banner on <see cref="ShouldEnumerate"/>.
    /// </summary>
    public static bool ShouldRegister(CallsAvailability availability) =>
        availability == CallsAvailability.Enabled;

    /// <summary>
    /// The tray's "Route calls to PC" item: what it says, whether it can be clicked, and whether it
    /// reads as on.
    ///
    /// <b>Three values from one call, because the two ways of getting this wrong are combinations.</b>
    /// An item that says "(needs MSIX)" and is still clickable invites a click that writes a setting
    /// nothing can honour; an item that reads as ticked while no registration can ever succeed is the
    /// shape this replaces - the user sees the switch as on, the calls half goes on retrying, and
    /// nothing anywhere names the reason. Returned together, neither is representable.
    ///
    /// <b>Gated on package identity, deliberately not on <see cref="Decide"/>.</b> The verdict answers
    /// <see cref="CallsAvailability.DisabledBySetting"/> the moment calls are switched off, so an item
    /// disabled on that would disable itself on the click that turned it off - a switch with exactly
    /// one use. The setting decides only the tick.
    ///
    /// The unpackaged text is the packaged one plus the reason, so the item stays recognisable as the
    /// same switch rather than reading as two unrelated entries. "(needs MSIX)" and not
    /// <see cref="Explain"/>: that runs to a couple of hundred characters, which is a menu entry
    /// wider than the screen, and the log is where it belongs.
    /// </summary>
    public static (string Text, bool Enabled, bool Checked) MenuItem(bool isPackaged, bool enableCalls) =>
        isPackaged
            ? ("Route calls to PC", true, enableCalls)

            // Unticked as well as disabled. The setting may well be true - it defaults to true and an
            // unpackaged run never gets to change it - but the tick is the app's claim about what it
            // is doing, and unpackaged it is doing nothing.
            : ("Route calls to PC (needs MSIX)", false, false);
}
