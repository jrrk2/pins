Tenth test build of PI.N.S. with native Celestron Origin camera, mount and focuser drivers, Touch-N-Stars and the Advanced API plugin. It replaces `origin9`.

**Needs** Ubuntu 26.04 or newer on a 64-bit PC (amd64, not a Raspberry Pi). Earlier test builds installed on Ubuntu 24.04 and Debian 13; this one does not. On those, or any other Linux, build the package from source: see [Celestron Origin test package](https://github.com/jrrk2/pins/blob/origin-direct-driver/BUILD_PINS.md#celestron-origin-test-package-linux-x64-from-a-clean-clone) in `BUILD_PINS.md` (`make release`, about 5 minutes once the prerequisites are installed).

### Install and run
1. `sudo apt install ./pins-origin_3.3.0.1058-nightly+origin10_amd64.deb` (this also installs the libgphoto2 libraries, and astrometry.net and its Tycho-2 index files, about 140 MB, which PI.N.S. now uses to plate solve).
2. Close the Celestron app, then run `pins` and open the address it prints (`http://localhost:5000`, or the PC's address from a phone).
3. **Set your location in Settings first.**
4. In Equipment choose Celestron Origin Camera, Mount and Focuser and connect. The telescope is found automatically on the same network; otherwise start it with `ORIGIN_HOST=<telescope address> pins`.

### If http://localhost:5000 does not open
- Start PI.N.S. by typing `pins` (it is installed in `/usr/bin`). **Do not run `/opt/pins/NINA` directly:** `pins` installs the Touch-N-Stars web page in your home folder before starting, and without it nothing answers on port 5000.
- `ss -ltn | grep 5000` shows whether it is listening. Stop PI.N.S. with Ctrl-C in the terminal where `pins` runs.
- Logs are in `~/.local/share/NINA/Logs/`; please attach the newest when reporting a problem. Messages about `indiserver` or camera SDKs (ZWO, QHY and others) are harmless for the Origin.

### New since origin9
- **Unpark straight after switching the telescope on.** When switched on, the telescope starts an initialisation of its own, and until it finished Unpark failed at once (the telescope refused with error -73; seen on 13 November). Unpark now waits for that initialisation to end, whether or not it succeeds, and then aligns as usual.
- **The model builder leaves out points behind trees and buildings.** With a custom horizon in the profile (Touch-N-Stars' Horizon File), the golden spiral drops points less than 1 degree above the horizon, and keeps the number of points asked for, spread over the open sky. On 12 November a point at 27 degrees in the north, behind trees, failed twice and cost 3.5 minutes. Without a horizon nothing changes.
- The package is now built from a clean clone of the repository, with everything else it includes fetched at pinned versions, and tried on OriginSimulator before release.

### New in origin9
First tried on the real telescope on the morning of 12 November: Unpark, autofocus, an 8-point model in twilight, a goto that put Jupiter 3.3 arcminutes from the centre, tracking, and Park within 3.2 arcminutes of the pole. These are the fixes from that session.
- **Fewer pointless plate solves.** A frame with fewer than 5 stars (a planet in twilight, cloud) is no longer plate solved. Before, each such frame on the camera page took a minute to fail, with the capture button spinning all the while.
- **The nebula filter:** with `~/.config/origin/filter` saying `nebula`, Unpark and the model builder take their plate-solve frames at ISO 1000 instead of 800. The filter passes only a small part of a star's light, and in twilight some frames had too few stars to solve. (Their exposures stay at 2 s: the mount stands still for these frames, so a longer exposure would only trail the stars.)
- The camera's shortest exposure is now 50 microseconds.

### Tips from the first real session
- **Focus before Unpark.** Unpark skips the telescope's own initialisation, focus search included. If the stars are out of focus, Unpark cannot solve: run Autofocus first.
- **Check the focal length in Settings is 335 mm** (the Origin's). It showed 353 mm, which spoils the web page's own plate solves and the focal length in saved FITS headers.
- To get the target name into saved file names, add `$$TARGETNAME$$` to the file pattern in Settings, Imaging.
- The web page cannot yet compute planet positions; type in their coordinates.

### New in origin8
- **DSLR cameras (libgphoto2) no longer need `libgphoto2-dev`.** PI.N.S. opened gphoto2 by a name only that development package provides, so without it no DSLR was found. The package now depends on the gphoto2 runtime libraries, which apt installs, and uses the system's copy instead of one bundled with OpenCV.

### New in origin7
- **Unpark is now a quick gate.** After skipping the telescope's own initialisation it raises the tube to 60 degrees (the parked tube may face a wall or a tree), takes one plate solve there and unparks. Pressing Unpark again while it runs no longer starts a second alignment; it waits for the first. `UNPARK_ALIGN=3` is gone: build a model instead.
- **A pointing model builder**, in Touch-N-Stars' 10micron model-builder screens. Enable the **10micron Model Builder** plugin in Touch-N-Stars' settings, then:
  - **Builder:** generate a golden spiral of points (12 by default, 25 to 80 degrees altitude), Build, Stop or Cancel, each point's state shown as it goes. Points are visited in an order that keeps the cables from winding; each is exposed for 2 s and solved while the mount moves to the next. A 10-point build takes about 4 minutes.
  - **Model:** the base tilt (in the polar-axis rows), cone error, rms and each point's error; Delete worst star; Clear alignment.
  - **Library:** save, load and delete named models (in `~/.config/origin/models`). A saved model only fits while the mount stands exactly as it did.
  - **Mount:** the 10micron mount settings answer "The Celestron Origin has no such setting".
  - During a build the mount reports slewing, and refuses other slews and syncs.
- **Faster slews:** both axes now move at once.

### New in origin6
- **Plate solving in the web page works with astrometry.net.** The Solve button, centring and sequences use the profile's solver, which was ASTAP; with no ASTAP installed every solve failed with "ASTAP executable location missing". The Local (astrometry.net) solver now runs `solve-field` directly on Linux, using the index files this package already installs. A profile left on ASTAP with no ASTAP installed solves with astrometry.net instead, and new profiles default to it. Nothing needs changing in the settings.
- Check the telescope's focal length in the settings is 335 mm (the Origin's). astrometry.net is told the plate scale to within 0.2 arcseconds per pixel, so a value far from 335 makes solves fail.

### New in origin5
- **Fixed: a sync after a slew no longer spoils the pointing.** A sync (for example coordinates typed in after a slew) was matched to the last frame taken, even when that frame was of another part of the sky before the slew; one such sync put gotos 47 degrees out and made Park miss the pole. A frame now counts only if it was taken since the mount last moved.

### New in origin4
- **The mount reports parked until a plate solve has succeeded**, even when the telescope says it initialised itself after switching on. So Unpark in the web page always runs PI.N.S.'s own alignment, and if it cannot plate solve (cloud, or no index files) it fails with a message instead of leaving gotos on a made-up frame. Unpark again when the sky clears.
- **Centring converges in one or two plate solves** anywhere in the sky, even before the pointing model is complete: each sync also corrects the pointing near where it was taken.

### PI.N.S. aligns the telescope itself (since origin3)
- **Unpark** no longer runs the telescope's own alignment (focus search, two stars of its choosing, and a level base assumed). It skips it, then takes **one plate solve** where the tube points, so that gotos land in the right part of the sky. On uneven ground build a model (above). `UNPARK_ALIGN=0` takes none.
- **Every plate solve and sync makes the pointing better**: Center + Solve, PlateSolvePlus "solve and sync", or a sequence's centring. Three syncs in different parts of the sky give the full model (base tilt, cone error, flexure); after that, gotos land within about 2 arcminutes.
- **Park** points the tube at the celestial pole and stops tracking, where the sun can never reach it if it is left out into the day.
- **Nothing moves while the sun is up.** (Testers using OriginSimulator in daylight: `ORIGIN_ALLOW_DAYLIGHT=1 pins`.)
- To use the telescope's own alignment instead: `ORIGIN_FAKE_INIT=0 pins`.
- **Cable-wrap protection**: the azimuth position when PI.N.S. first connects is taken as where the cables hang straight, and kept in `~/.config/origin/cordwrap.json`. Connect for the first time with the cables hanging straight; after unwinding them, delete that file and connect again.

### Please note
- **Unpark moves the telescope.** Keep a hand near the power the first time.
- If Unpark cannot plate solve (cloud, or no index files), it says so in the log (`~/.local/share/NINA/Logs`) and gotos go by the telescope's made-up frame until a plate solve and sync. Known: the web page may not show that, or other Unpark failures.

Checked on 2026-11-16 against OriginSimulator, installed from this .deb with a fresh profile: connect, Unpark with one plate solve in 16 s, a 2 s frame, a slew to the meridian that ended 0.3 arcminutes from the target, and Park.

SHA-256 in the `.sha256` file.
