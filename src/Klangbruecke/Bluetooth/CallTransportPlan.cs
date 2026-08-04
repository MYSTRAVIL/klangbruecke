using Klangbruecke.Platform;

namespace Klangbruecke.Bluetooth;

/// <summary>
/// How far down the calls path an availability verdict lets this run go.
///
/// Two steps, not one, because they need different things. Discovery - GetDeviceSelector,
/// FindAllAsync, FromId, IsRegistered - was exercised against the real phone with no package identity
/// and all of it worked; it is RegisterApp, claiming the hands-free role, that needs the restricted
/// phoneLineTransportManagement capability. So an unpackaged run still enumerates and logs what it
/// found, and skips only the registration.
///
/// That is worth a named rule rather than an inline condition: gating the whole block on Enabled is a
/// one-line change back, it looks harmless, and it costs the "dotnet run" log the one piece of
/// evidence - whether the phone's transport is discoverable at all - that the packaged build cannot
/// be trusted to produce for the first time under a restricted capability.
/// </summary>
public static class CallTransportPlan
{
    /// <summary>Enumerate unless the user turned calls off, in which case looking is pure noise.</summary>
    public static bool ShouldEnumerate(CallsAvailability availability) =>
        availability != CallsAvailability.DisabledBySetting;

    /// <summary>Register and connect only when nothing structural is in the way.</summary>
    public static bool ShouldRegister(CallsAvailability availability) =>
        availability == CallsAvailability.Enabled;
}
