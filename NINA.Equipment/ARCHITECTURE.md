# NINA.Equipment Architecture

## Purpose

`NINA.Equipment` is the hardware integration layer. It defines device abstractions and implements concrete adapters for cameras, mounts, focusers, guiders, domes, switches, weather devices, GNSS sources, planetarium integrations, and vendor SDK wrappers.

Build shape from `NINA.Equipment.csproj`:

- Target framework: `net10.0-windows`
- Output type: `Library`
- WPF enabled

## Top-Level Structure

- `Interfaces/`
  Device contracts such as `ICamera`, `ITelescope`, `IFocuser`, `IGuider`, `IDome`, `ISwitchHub`, `IWeatherData`, plus factories like `IGnssFactory` and `IPlanetariumFactory`.
- `Equipment/`
  Concrete implementations grouped by device category:
  - `MyCamera`
  - `MyTelescope`
  - `MyFocuser`
  - `MyFilterWheel`
  - `MyGuider`
  - `MyDome`
  - `MySwitch`
  - `MyFlatDevice`
  - `MyWeatherData`
  - `MyGPS`
  - `MyPlanetarium`
  - `MyRotator`
  - `MySafetyMonitor`
- `SDK/`
  Native/vendor interop wrappers for camera, focuser, filter wheel, and flat device SDKs.
- `Utility/`
  Discovery and integration helpers such as `ASCOMInteraction` and `AlpacaInteraction`.
- `Exceptions/` and `Model/`
  Equipment-specific support types.

## Implementation Pattern

The project follows a clear pattern:

- interfaces define the device surface
- category folders contain concrete implementations
- discovery helpers instantiate those implementations
- SDK folders isolate vendor-specific interop

Examples from the code:

- `Utility/ASCOMInteraction.cs`
  Enumerates installed ASCOM drivers and creates `AscomCamera`, `AscomTelescope`, `AscomFilterWheel`, `AscomFocuser`, and related wrappers.
- `Utility/AlpacaInteraction.cs`
  Discovers network devices through `AlpacaDiscovery.GetAscomDevicesAsync(...)`, wraps them in the same adapter types, and also exposes direct Alpaca clients such as `AlpacaDirectCamera`.
- `Equipment/MyCamera/GenericCamera.cs`
  Provides a reusable `ICamera` implementation on top of an `IGenericCameraSDK`.
- `Equipment/MyCamera/FileCamera.cs`
  Provides a non-hardware camera implementation backed by files on disk.
- `Equipment/MyGuider/MGENGuider.cs`
  Adapts the `NINA.MGEN.IMGEN` library into the guider abstraction.
- `Equipment/MyPlanetarium/PlanetariumFactory.cs` and `Equipment/MyGPS/GnssFactory.cs`
  Select concrete external integrations from profile settings.

## ASCOM, Alpaca, And Native SDKs

The code supports multiple backends in parallel:

- ASCOM COM drivers through `ASCOMInteraction`
- Alpaca discovery and direct network clients through `AlpacaInteraction`
- native vendor SDKs under `SDK/*`
- file-based or built-in utility devices like `FileCamera`

The `Equipment/AscomDevice.cs` base class is the shared adapter foundation for many ASCOM/Alpaca implementations.

## Special Integration: SBIG Camera Service

This project also contains the SBIG-specific camera service bridge:

- `Equipment/MyCamera/SBIGCamera.cs`
- `Equipment/MyCamera/SBIGCameraASCOMService.cs`

That code uses the protobuf/gRPC camera contract generated in `NINA.Core` plus `GrpcDotNetNamedPipes`. This is an exception to the otherwise simple adapter model and is worth preserving as equipment-layer code rather than moving into UI projects.

## Special Integration: Celestron Origin

`Equipment/Origin` talks to the telescope directly: a WebSocket for JSON commands and status, and HTTP for frames. `OriginBackend` and `TelescopeData` are the C# port, by the original author, of `OriginBackend`, `TelescopeDataProcessor` and the device operations of `ControlServer` from [OriginMonitor](https://github.com/jrrk2/OriginMonitor). The camera, mount and focuser drivers share one `OriginBackend` through `Acquire`, because the telescope accepts one client: an `OriginHeadless` daemon or the Celestron app on the same telescope excludes PI.N.S. `OriginDiscovery` finds the telescope from its UDP broadcast, or from `ORIGIN_HOST`.

**Licensing rule: do not port or copy manual focus code from OriginMonitor.** OriginMonitor is GPL only because its manual focus commands (`doFocuserMove`, `queryFocuser`, `GetFocuserAdvancedSettings`, added in commit `bc381f3`) came from Origin Live View, which is GPL. Everything else was written by the author and is available to this MPL 2.0 project. Focuser status and the firmware's own autofocus predate that import and are ported. `indi-celestron-origin` in indi-3rdparty, by the same author, has no focuser commands and is a clean reference for the rest of the protocol.

Manual focus here (the `focuser` region of `OriginBackend`, and `OriginFocuser.Move`) was instead worked out, at the author's request, from a packet capture of the Celestron iOS app driving a real telescope: `origin_anal/origin1.txt` (sha256 `3fc89f37c21a8d3c46ee6512824d5c8883f930d3abb7a5257f3252c247a8dc33`, 2025-12-17, ten months before that import), frames at 621-658 s. Change it from further captures or tests on the telescope, not from OriginMonitor. It was written by someone who had read `doFocuserMove`, so it is not a formal clean-room implementation; what makes it independent is that each behaviour traces to the capture:

- `GetPositionLimits` gives `Min` and `Max` (1975 and 37527 on that telescope). `MoveTo` with an absolute `Value` is acknowledged in about 30 ms.
- `Focuser` status arrives about once a second unasked, so nothing is polled. `IsMoveToOver` is false during a move and true on arrival. The capture has two moves, both ending exactly on target.
- A move down overshoots by about 500 steps and comes back up (18596 to 17000 went through 16511), so the firmware takes up backlash itself and PI.N.S. backlash compensation should stay off. Speed is about 355 steps a second.
- The capture has no focuser stop, no move sent during another, and no move outside `IDLE`, so `Halt` does nothing and a move waits for the others to end. All of these are limits of one capture, not known firmware behaviour.

Rules that came from the real firmware and are easy to undo by accident:

- Do not use `AbortAxisMovement` or `StopAll`. Firmware 1.5.x answers `Unknown command` to both, although OriginMonitor and the INDI driver send the first and OriginSimulator accepts it. `AbortMotion` sends `Slew` 0/0 to `Mount` first, because zero speed is the stop and must not wait on anything, then `HaltTasks` to `TaskController`, then `Slew` 0/0 again 300 ms later. The Celestron app sends the first two together (`origin1.txt`, 1066 s). Tested on the real telescope on 2026-11-07, 5 s into a goto at about 4 degrees a second on both axes: altitude stopped within about 0.5 s and azimuth within about 1.5 s, about 75 degrees short of the target, and the firmware reported `Precise GoTo cancelled` (code -7), which is expected. Because the commands go out together, that test does not show which of them is needed. The mount's alignment was fake for this, which should not matter to the stop.
- The simulator answers `ErrorCode 0` to any command it does not know, so a passing simulator test says little about whether the firmware accepts a command. `OriginFakeTelescope` answers `Unknown command` instead.
- A goto finishing is not the end of a slew: the firmware then centres and may autofocus, so `SlewToCoordinates` waits for `TaskController` to report `IDLE`.
- The camera resets exposure, ISO and bit depth after a capture, and a frame taken with the reset values comes back as a PNG. `TakeFrame` resends them and asks once more on a PNG or on a FITS header showing other settings.
- A camera can hang in `SAMPLE_CAPTURE`, acknowledging `CancelImaging` and ignoring later captures. Only a `Reboot` clears that, and the mount then comes back unaligned, so it is not done automatically.
- Relative motion (`MoveAxis`, sent as `Slew` rates -9 to 9) works before initialisation on the firmware; gotos do not. Measured on the encoders of the real telescope (2026-11-07), in degrees a second: rate 2 about 0.004, 4 about 0.033, 5 about 0.068, 6 about 0.27, 7 about 0.97, 8 about 2.0, 9 about 4.0 on both axes. Positive `AltRate` raises the tube and positive `AzmRate` increases the reported azimuth. After `AbortMotion` at rate 9 the axes were still within about 2 seconds, about 2.2 degrees further in altitude.  Slew pauses tracking, and it resumes after the stop. The tube's travel limits are not known or enforced here. ninaAPI's axis socket refuses to move a mount reporting `AtPark`, and an uninitialised mount reports `AtPark` so that Unpark can initialise it, so from the web page relative motion is only reachable after Unpark.
- The firmware starts an initialisation by itself after a reboot, and answers `RunInitialize` with code -73 while one is running, so `Initialize` waits for it instead of failing. Its first step can be a focus search over thousands of steps (15560 to 22337, 2026-11-07). Under cloud it ended with `Failed to find focus!` (code -36) and returned the focuser to where it started, with the mount unaligned.
- Frames are taken one at a time: `TakeFrame` downloads each before the next is requested. Measured on 2026-11-07 (`OriginRealTelescopeTest.Step6_SnapshotDutyCycle`), 10 s frames took 11.74 s each, 85% of the time exposing; about 1.15 s of each cycle is the download of the 12.5 MB FITS. OriginMonitor's sequencer reaches 87-96% by requesting the next frame as soon as one is ready. Doing that here means starting a frame before PI.N.S. has asked for it, and cancelling it when the next request differs, which risks the `SAMPLE_CAPTURE` hang, so it has not been done.
- `ORIGIN_FAKE_INIT=1` makes Unpark send `FakeInitialize: true` and skip raising the tube, for testing. **It does not mean that nothing moves.** On 2026-11-07, from an unaligned mount at 71.5° altitude, the firmware reported `MOVING_MOUNT` and lowered the tube to 45° in about 8 s, with `IsAligned` already true for all of that move (`websocket_log_20261031_084107` and `websocket_log_20261015_184459` show it finishing in under half a second with the encoders unchanged). It then reports `IsFakeInitialized`, tracks, and accepts gotos, with an invented azimuth frame. It was accepted with the sun up, where a real initialisation stops with "Sun is above horizon", so it removes that protection.
- A message updates only the fields it carries. OriginMonitor reads absent fields as defaults, which after a command's acknowledgement would report a slew in progress and a mount at RA 0.
- The telescope drops the link when pings stop, whatever TCP is doing, and a lost Wi-Fi link closes nothing on this side. `KeepAliveTimeout` handles both: without it .NET sends unsolicited pongs rather than `PING` frames. Its abort reaches a pending receive as `OperationCanceledException`, which must not be mistaken for a deliberate shutdown. Nothing may block the receive loop.
- `Sync` returns false (`SyncToRaDec` has never been seen working), which makes `CenteringSolver` use a measured correction.
- Status field units differ: the firmware reports the site in radians and the simulator in degrees, which the mount driver rejects in favour of the profile's site.
- Commands go out with sorted keys and whole numbers as integers, as Qt wrote them, which is the only form this firmware has been seen to receive. A test compares them with lines from a real log.

`NINA.Test/Equipment/Origin` has tests against a fake telescope. `OriginSimulatorIntegrationTest` runs end to end when `ORIGIN_SIM` names OriginSimulator, and refuses port 80, where a real telescope listens. `OriginRealTelescopeTest` is what only the telescope can show, from harmless to moving, when `ORIGIN_REAL` names it; its moving steps are `[Explicit]`.

## Dependency Position

Project references:

- `NINA.Core`
- `NINA.Astrometry`
- `NINA.Image`
- `NINA.MGEN`
- `NINA.Profile`
- `nikoncswrapper`

That dependency set is consistent with the code:

- shared models/utilities from `NINA.Core`
- coordinate/math types from `NINA.Astrometry`
- exposure/image conversion types from `NINA.Image`
- guider transport for MGEN from `NINA.MGEN`
- runtime settings from `NINA.Profile`

There is no dependency on the main app shell or plugin loader.

## Contribution Notes

- Put new device interfaces in `Interfaces/` before adding implementations.
- Keep vendor DLL interop and P/Invoke code under `SDK/` or device-specific implementation folders.
- Use the existing discovery helpers for ASCOM/Alpaca-backed devices instead of inventing new scanning paths.
- UI-facing mediators and view models do not belong here; those live in `NINA.WPF.Base` and `NINA`.
- If a device needs persisted settings, wire it through `IProfileService` rather than storing state inside the adapter alone.
