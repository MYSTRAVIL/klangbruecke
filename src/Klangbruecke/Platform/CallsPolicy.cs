namespace Klangbruecke.Platform;

public enum CallsAvailability
{
    Enabled,
    DisabledBySetting,
    DisabledNoPackageIdentity,
}

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
}
