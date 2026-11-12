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

`Equipment/Origin` talks to the telescope directly: a WebSocket for JSON commands and status, and HTTP for frames. `OriginBackend` and `TelescopeData` are the C# port, by the original author, of `OriginBackend`, `TelescopeDataProcessor` and the device operations of `ControlServer` from [OriginMonitor](https://github.com/jrrk2/OriginMonitor). The camera, mount and focuser drivers share one `OriginBackend` through `Acquire`. The telescope accepts more than one client at a time (2026-11-08: PI.N.S. and test scripts together), but two clients commanding the mount at once will conflict, so do not run `OriginHeadless` or the Celestron app alongside PI.N.S. `OriginDiscovery` finds the telescope from its UDP broadcast, or from `ORIGIN_HOST`.

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
- The firmware reports the site in radians. OriginSimulator did so in degrees until 2026-11-09; the mount driver rejects implausible values in favour of the profile's site.
- Commands go out with sorted keys and whole numbers as integers, as Qt wrote them, which is the only form this firmware has been seen to receive. A test compares them with lines from a real log.

### PI.N.S.'s own calibration

`OriginCalibration` aligns the telescope from PI.N.S.'s own plate solves, instead of the firmware's initialisation, which needs a clear sky at two places the firmware chooses and assumes a level base (on grass on 2026-11-08 the base was 5 degrees out). The firmware is only asked to move and to follow a track.

Firmware facts this rests on, measured on the real telescope on 2026-11-08:

- Gotos and `TrackMovingTarget` need `TaskController` `IsReady`; `IsAligned` with one reference is not enough (-5 otherwise, in all 57 gotos in the logs). The mount driver's `aligned` uses `IsReady` when the firmware reports it.
- A real initialisation adds references only with the live stream on (-53 `Can't add alignment reference` otherwise), and PI.N.S. disables it at connect, so Unpark turns it on for a real initialisation and off again afterwards.
- After a fake initialisation the firmware's frame is the encoders plus two constants: altitude = `Enc1` + 40.377 and azimuth = `Enc0` + 74.616 in 3467 readings, declaring the pointing at that moment to be azimuth 180 at the tilt sensor's altitude. Its RA/Dec are J2000 (the difference from date coordinates matched precession exactly).
- `TrackMovingTarget` takes `TargetTrack: [{Ra, Dec (J2000 radians), JulianDate (UTC)}]` and the firmware follows it to about 0.6 arcminutes on the encoders; it starts with about 6-13 s of `CENTERING_TARGET`. `StopTracking` is `Unknown command`; `HaltTasks` stops a track.
- The firmware did not plate solve or correct a track by itself, at least after a fake initialisation with the live stream off (the 20-minute Vega track, 20:56-21:16): centring reported only `CenteringStatus: SLEWING` and then `IDLE` throughout, the firmware took no images of its own, and the encoders kept to the track within 0.6 arcminutes while the field drifted 1.5 arcminutes on the sky. Not yet measured: the same with the live stream on, or after a real initialisation, where centring may include solving. Running `OriginTrackGuider` against a firmware that also corrects would fight it.
- The tilt sensor read the tube's true altitude plus 1.15 degrees.

The parts:

- `OriginPointingModel`: where the tube points for given encoders. The mount frame is azimuth `Enc0`, elevation a - F cos(a) with a = `Enc1` + K, and a cone error C (the optical axis off square to the altitude axis, an azimuth error growing as 1/cos(altitude)); a unit quaternion, from Horn's closed form (J. Opt. Soc. Am. A 4, 629, 1987), takes it to the true horizon and absorbs the base's tilt and the azimuth zero. K, F and C are found by a pattern search around Horn's rotation: K and F trade off along a narrow valley, and a fixed window round a coarse grid's winner missed it (2026-11-09, 2.0 arcminutes instead of 0.33; regression test `Fit_FollowsTheValleyWhereAltitudeZeroAndFlexureTradeOff`). Flexure is fitted from 5 points and cone error from 6; three points are a quick start.
- `OriginSky`: J2000 to the horizon and back through NINA's SOFA transforms, with refraction from the telescope's own temperature and humidity sensor (`SofaOriginSky.ForOrigin`; no barometer, so the standard atmosphere's pressure at the site's elevation). The model maps encoders to the apparent sky, where the tube points. The firmware's frame is converted without refraction, for tracks handed to it: whether the real firmware refracts is not known, and a constant altitude offset during a track would show it.
- `OriginCalibration`: moves by encoder feedback (`MoveTo`, rate ladder 9/8/7/6, stopped by zero speed twice), `Calibrate` (each plate solve runs while the mount moves to the next point), `Refine`/`GotoAndRefine` (each goto's check frame becomes a point; one more than 3 degrees from the model is turned away as a bad solve or a slipping clutch; a new point within 2 degrees of an old one replaces it), `Goto` (leads the target by the estimated slew time, because nothing tracks during or after these moves), and `BuildTrack`/`Track` for a fixed target or an ephemeris (comets, satellites).
- `OriginAstrometryNet`: `solve-field` on the raw Bayer FITS, binned by two, with the 4208-4211 indices (`ORIGIN_INDEX_DIR`, default `~/astrometry_indices`).
- `OriginCordWrap`: see below.
- `OriginTrackGuider`: guiding and dithering from background plate solves. The correction learnt from the solves and N.I.N.A.'s own dither offset (`DitherOffsetSelector`) are added into a fresh `TrackMovingTarget`. Frames come from imaging through `Feed`, so it never takes the camera.

Timings: a quick three-point calibration takes about 2.5 minutes at the real speeds (mostly the two 120-degree slews), and each later goto adds a point at no extra cost. On OriginSimulator with a secret mount (cone, flexure, periodic error, refraction), nine points fitted the tilt, its direction, the altitude zero and the flexure to the truth's last digit, and pointed anywhere in the sky to 0.4 arcminutes (2026-11-09). On the real telescope only the parts have run so far: a 9-point model to 0.8 arcminutes before the clutch slipped, and a Vega track held for 20 minutes 4-5 arcminutes off centre with a 5-point model.

### The mount driver's own pointing

`OriginTelescope` uses `OriginCalibration` itself, so that the web page and sequences get it with no buttons of their own:

- **Unpark** asks for the firmware's fake initialisation by default (no focus search, no star alignment, works whatever the ground), then brings the tube to 60 degrees altitude by the tilt sensor (clear of trees and walls, and far enough from the zenith for one solve to fix the azimuth) and takes one plate solve there, for a level-base model from that point (`UNPARK_ALIGN=0`: none). Without a tilt reading it solves where the tube points. Unpark only gates the mount; a fuller model comes from the model builder or from syncs. A second Unpark while one runs waits for it and gets its result: the web page shows no progress, and on 2026-11-09 three presses ran three alignments at once, which drove the mount to each other's points and spoiled all three. `ORIGIN_FAKE_INIT=0` makes Unpark run the telescope's own initialisation instead, which is also the driver action `CelestronInitialize`.
- **Slews** go by PI.N.S.'s model when it has one: a move by encoder feedback, then a moving-target track by the model, renewed before it ends. The reported RA/Dec and alt/az come from the model too.
- **Sync** (N.I.N.A.'s centring, Touch-N-Stars' Center + Solve and PlateSolvePlus "solve and sync") adds a point, paired with the encoders at the last frame's mid-exposure, not at the sync: the solve takes 10-30 s, and meanwhile the mount has moved on. Below three points the model is a level-base one from the newest point; three distinct places make the fitted model. On a base 9.9 degrees out of level (OriginSimulator, 2026-11-09), centring with the level model settled 3-4 arcminutes out (it tracks off by about 0.26 arcminutes a minute per degree of tilt); from the third place it centred to 0.3 arcminutes and slewed to other stars within about 2.
- **Parked until a plate solve has succeeded.** With the fake initialisation the mount reports parked, even when the firmware says it is aligned (it initialises by itself after a reboot), until PI.N.S. has a model; the web page will not unpark an unparked mount, and without a solve every goto would go by the invented frame. Unpark that cannot solve fails and says so. `UNPARK_ALIGN=0`, or the telescope's own initialisation, unparks without one.
- **A local correction from the last sync**: what the model still misses at the synced point, applied in full within 5 degrees of it and fading out by 15. Centring syncs at one place replace each other rather than adding points, and a model without enough points lacks flexure and cone terms, so without it centring stuck at 8 arcminutes on a mount with strong flexure; with it, every centring converged in one or two syncs (OriginSimulator, 2026-11-09).
- **Park** points the tube at the celestial pole and stops tracking: the sun, never more than 23.4 degrees from the equator, cannot come within 66.6 degrees of it, and the pole does not move. That matters more than unwinding the cables, which go whichever way keeps them within the limit. After a fake initialisation with no plate solve yet, Park is refused.
- **Nothing moves with the sun up** (Unpark, slews, Park, axis moves; stopping always works), because a fake initialisation lacks the firmware's own "Sun is above horizon" refusal. The sun's altitude comes from the Astronomical Almanac's low-precision formulas, not N.I.N.A.'s NOVAS, which without its `JPLEPH` file returns a wrong position rather than failing. `ORIGIN_ALLOW_DAYLIGHT=1` lifts this for OriginSimulator.
- The plate solver is `solve-field` with index files from `ORIGIN_INDEX_DIR`, `~/astrometry_indices`, `/usr/share/astrometry` or `/usr/local/share/astrometry`; the package recommends Ubuntu's `astrometry-data-tycho2-07-littleendian` (22-30 arcminute quads, for the Origin's 75 by 50 field).
- **The model builder** (`OriginModelBuilder`, `OriginTelescope.ModelBuilder`) is Touch-N-Stars' 10micron model-builder screens for the Origin. With no 10micron plugin loaded, the plugin's `TenMicronController` answers from the Origin in pins builds (`TenMicronController.Origin.cs`, under the `PINS` constant; on the fork `jrrk2/N.I.N.A-Plugin-for-Touch-N-Stars`). It generates a golden spiral of points between altitude limits (equal area, 25 to 80 degrees and 12 points by default), visits them in one sweep of azimuth within the cord-wrap limit, solves each in the background while the mount moves on, and refits after every solve, so later points are aimed better; points that fail are tried again at the end. During a build the mount reports slewing (the web page then holds back its own moves and syncs) and refuses slews, Park, Unpark and axis moves. Delete-worst, clear (back to parked until a plate solve), and saved models (JSON files in `models/` beside `cordwrap.json`, valid only while the mount stands as it did) are there too. On the Model tab the 10micron "polar axis" figures are the base's tilt, the alt-az mount's equivalent, and "cone error" is the fitted cone; the Mount tab's 10micron settings are not on the Origin and answer that.
- **Plate-solve frames with the nebula filter.** Unpark's and the model builder's frames are taken with the mount still (the solve is paired with the encoders at mid-exposure), so the sky trails across them at up to 15" a second: up to 20 px in 2 s. A longer exposure only lengthens the trails, so with `filter` saying "nebula" these frames go to ISO 1000 at least (`OriginBackend.SolveIso`) and keep their exposure. On 2026-11-12 the nebula filter left 2 s twilight frames with too few stars for some solves.
- The camera's shortest exposure is 50 microseconds (`OriginCamera.ExposureMin`); above that, any whole number of microseconds.
- **Moves under encoder feedback** drive both axes at once, each stopping when its time is up, in steps of the rate ladder; the second, shorter pass of a goto costs only a status read when the first lands within the tolerance.

### Cord wrap

On 2026-11-08 moves driven the short way round, with only their targets checked against a limit, added up to almost three turns (about 1035 degrees): the external power cord wrapped round the azimuth drive and snapped after about two turns (about 720 degrees), and the azimuth clutch slipped (the sky then turned 0.58-0.70 of what the encoder said, until the clutch was tightened). The firmware's own gotos and tracks also turn the short way round, so only the host can keep the cables safe.

- `OriginCordWrap` keeps the running count of azimuth from cable-neutral, following `Enc0` from one status message to the next. The neutral is set by the person at the telescope after unwinding the cables, and kept in `cordwrap.json` beside the filter file. Resuming assumes the mount turned less than half a turn while nothing was listening.
- The cables are safe within 180 degrees either side, and all of that is needed, or part of the sky cannot be reached. So the margin comes from the speed instead: `MarginFor` (12 degrees at rate 9, 6 at 8, 3 at 7, else 1) for `MoveAxis` and the mount's poll, which stops an outward manual slew.
- Moves under encoder feedback are planned in the count, never with a wrap-around, so they take the long way round when the short one would cross the far side.
- A track may start only within the limit; a target that reaches the limit is followed on past it, up to `TrackOverrun` (45), so that the observation is finished, and the next move comes back the long way. `Drive` stops only an outward move past the limit, or that return could not start.

### Guiding hardware

The mount's guide input is hardware: a standalone autoguider such as an SSAG has its own guide camera and pulses the mount directly, with no host software, so PI.N.S. can neither see nor command it. No firmware pulse command is known, so `OriginTelescope` does not implement `PulseGuide` (`CanPulseGuide` is false), and N.I.N.A.'s Mount Dither cannot work. With an autoguider running, PI.N.S. cannot dither, and `OriginTrackGuider` must stay off: the autoguider would pull back its corrections and dithers. Without one, `OriginTrackGuider` held the target to 3 arcseconds and dithered to within 2.3 on OriginSimulator. `NINA.MGEN` (an autoguider that dithers on command) is in the solution, but no guider here uses it.

### Tests

`NINA.Test/Equipment/Origin` has tests against a fake telescope (`OriginFakeTelescope`) and a simulated mount moving at the measured speeds; the SOFA and NOVAS libraries are copied into the test output, so they need no `LD_LIBRARY_PATH`. Against OriginSimulator (when `ORIGIN_SIM` names it, refusing port 80, where a real telescope listens):

- `OriginSimulatorIntegrationTest`: the drivers end to end.
- `OriginSimulatorCalibrationTest`: fake initialisation, frames that plate solve where the tube really points, cord wrap, the full calibration chain scored against the simulator's secret mount (read from `/sim/truth` only afterwards), moving targets and a track carried past the cord limit and back. About 25 minutes at the real speeds.
- `OriginSimulatorModelBuilderTest`: Unpark pressed twice, a 10-point build scored against the secret level error, a goto by the built model, delete-worst, save, clear, load and delete a model, Park on the pole.
- `OriginSimulatorDitherTest`: the plate-solve guider holding a target and dithering; Mount Dither, which fails for the reason above.

OriginSimulator (branch `mount-model`) simulates the mount as a machine for these: encoders on a base with a secret level error up to 10 degrees, cone, flexure, periodic error and tilt-sensor offset; the firmware's frame after a real or fake initialisation; gotos and tracks the short way round; a cable count that logs each pass of the 180-degree limit (cord-wrap detection); Gaia DR3 stars to G 12 in the frames, refracted, in the true projection, integrated through the exposure (field rotation, trails), oval for 30 s after a slew. Its README lists the settings. The simulator still answers `ErrorCode 0` to commands it does not know, so passing there says nothing about whether the firmware accepts a command.

`OriginRealTelescopeTest` is what only the telescope can show, from harmless to moving, when `ORIGIN_REAL` names it; its moving steps are `[Explicit]`. `Step7_OwnCalibrationGotoAndTrack` runs the quick calibration, refines it at six bright stars, then a goto and a track, each checked by plate solves, to compare with the firmware's own initialisation. It needs a neutral in `cordwrap.json`, a clear sky, and mains power or a fresh battery (the battery ran out mid-calibration on 2026-11-08).

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
