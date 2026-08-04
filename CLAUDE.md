# Klangbruecke — project instructions

Windows tray app: phone → PC audio over Bluetooth. Music (A2DP sink) + cellular calls
(HFP hands-free), in one app, on the built-in radio.

## Before changing anything about the approach

**Read `docs/FINDINGS.md` first.** It records what was empirically verified on this machine versus
what the internet claims. Three specific traps:

1. Do **not** implement LAF token generation — Microsoft removed `PhoneLineTransportDevice` from the
   LAF list. It needs no token.
2. Do **not** propose BTstack, a USB dongle, or Zadig/WinUSB. The inbox stack works. The dongle path
   regresses Bluetooth range and rebinding the internal radio kills every other BT device
   (game controllers).
3. Do **not** suggest upgrading to Windows 11 to "fix" the call API. Calls already work on 19045, and
   Win11 has a documented regression affecting the A2DP-sink capture endpoint this app depends on.

## When something doesn't connect

Check the pairing before suspecting the code. The stale-IRK bug (`docs/FINDINGS.md` §3) presents
exactly like an API failure. Look at `BTHUSB` events 35 / 16 / 24 in the System log first.

Never trust a UI "connected" indicator — verify with `Get-PnpDevice`.

## Conventions

- Target `net8.0-windows10.0.19041.0`. Do not raise the minimum — 19041 is the floor for both WinRT
  APIs and the dev machine is 19045.
- ASCII `Klangbruecke` everywhere: folder, namespace, assembly, package identity, display name.
  No umlaut anywhere.
- MSIX packaging is load-bearing, not cosmetic — the restricted capability requires package identity.
  Build via `packaging/Build-Msix.ps1` (uses Windows SDK `makeappx`/`signtool`, no Visual Studio).
- Tray-first. The app must run headless in the tray and auto-start. A window that must stay open is
  the exact failure of the app this replaces.

## Testing

The two halves are independent and should be tested independently:

- **Music** — connect, then confirm `Line (<phone> A2DP SNK)` appears under
  `Get-PnpDevice -Class AudioEndpoint`, and that the PC appears in the phone's output picker.
- **Calls** — place a real call. Check audio both directions; the mic half fails silently otherwise.

Reconnect-after-reboot and phone-initiated reconnect are the historically fragile paths — the
predecessor app's defining bug. Test them explicitly rather than assuming.
