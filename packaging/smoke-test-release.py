#!/usr/bin/env python3
"""Drives a running PI.N.S. through the Advanced API the way Touch-N-Stars does, against OriginSimulator: connects the Origin mount and camera,
unparks (a fake initialisation and a plate solve), takes a frame, slews to the meridian, parks and disconnects. Exits non-zero at the first step that fails.

Usage: smoke-test-release.py [api]    api defaults to http://localhost:1888/v2/api; PINS_LOGS names PI.N.S.'s log folder (default ~/.local/share/NINA/Logs).
Started by "make release-test", which also starts PI.N.S. and the simulator.
"""
import glob, json, math, os, sys, time, urllib.error, urllib.parse, urllib.request

API = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:1888/v2/api"
LOGS = os.path.expanduser(os.environ.get("PINS_LOGS", "~/.local/share/NINA/Logs"))
SITE = {"Latitude": 52.2, "Longitude": 0.1, "Elevation": 20}    # any site works: PI.N.S. hands its own to the simulator


def get(path, timeout=60, **query):
    url = API + path + ("?" + urllib.parse.urlencode(query) if query else "")
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.load(r)


def ok(path, **query):
    reply = get(path, **query)
    if not reply.get("Success"):
        raise SystemExit(f"FAIL {path}: {reply.get('Error')}")
    return reply["Response"]


def wait(what, condition, timeout):
    start = time.time()
    while time.time() - start < timeout:
        if condition():
            print(f"  {what}: {time.time() - start:.0f} s", flush=True)
            return
        time.sleep(2)
    raise SystemExit(f"FAIL {what}: not within {timeout} s")


def mount():
    return ok("/equipment/mount/info")


def step(title):
    print(f"{time.strftime('%H:%M:%S')} {title}", flush=True)


step("Waiting for the Advanced API")
def api_up():
    try:
        return get("/version", timeout=5).get("Success")
    except (urllib.error.URLError, OSError):
        return False
wait("Advanced API answering", api_up, 180)
print(f"  PI.N.S. {ok('/version/pins')}")
try:
    urllib.request.urlopen("http://localhost:5000/", timeout=10).read(1)
    print("  Touch-N-Stars answering on port 5000")
except (urllib.error.URLError, OSError) as e:
    raise SystemExit(f"FAIL Touch-N-Stars on port 5000: {e}")

step("Setting the site")
for name, value in SITE.items():
    ok("/profile/change-value", settingpath=f"AstrometrySettings-{name}", newValue=value)

for device, id in (("mount", "Origin_Mount"), ("camera", "Origin_Camera")):
    step(f"Connecting the {device}")
    ok(f"/equipment/{device}/rescan")
    ids = [d.get("Id") for d in ok(f"/equipment/{device}/list-devices")]
    if id not in ids:
        raise SystemExit(f"FAIL {id} not among the {device}s {ids}: is ORIGIN_HOST set to the simulator?")
    ok(f"/equipment/{device}/connect", to=id)
    wait(f"{device} connected", lambda: ok(f"/equipment/{device}/info")["Connected"], 120)

def unpark_failures():
    """How many times the log says Unpark failed: the Advanced API starts an Unpark and answers at once, so its failure shows only there."""
    logs = sorted(glob.glob(os.path.join(LOGS, "*.log")), key=os.path.getmtime)
    if not logs:
        return 0
    with open(logs[-1], errors="replace") as f:
        return f.read().count("Mount failed to unpark")


step("Unparking (fake initialisation and a plate solve)")
for attempt in (1, 2):    # a plate solve can miss, and the error says to unpark again, as a user would
    if not mount()["AtPark"]:
        break
    failures = unpark_failures()
    ok("/equipment/mount/unpark")
    wait("unpark ended", lambda: not mount()["AtPark"] or unpark_failures() > failures, 900)
    if mount()["AtPark"]:
        print(f"  Unpark failed (attempt {attempt}); the log says why", flush=True)
if mount()["AtPark"]:
    raise SystemExit("FAIL Unpark failed twice")

step("Taking a 2 s frame")
ok("/equipment/camera/capture", duration=2, omitImage="true")
def frame_ready():
    reply = get("/equipment/camera/capture", getResult="true", omitImage="true")
    return reply.get("Success") and isinstance(reply["Response"], dict)
wait("frame ready", frame_ready, 120)

step("Slewing to the meridian, 20 degrees from the zenith")
info = mount()
ra, dec = info["SiderealTime"] * 15, info["SiteLatitude"] - 20
ok("/equipment/mount/slew", ra=f"{ra:.4f}", dec=f"{dec:.4f}")
time.sleep(3)
wait("slew done", lambda: not mount()["Slewing"], 600)
info = mount()
miss = max(abs((info["RightAscension"] * 15 - ra + 180) % 360 - 180) * abs(math.cos(math.radians(dec))), abs(info["Declination"] - dec))
print(f"  at RA {info['RightAscension'] * 15:.3f} Dec {info['Declination']:.3f}, {miss * 60:.1f} arcmin from the target")
if miss > 0.5:
    raise SystemExit(f"FAIL the mount reports {miss * 60:.1f} arcmin from the target")

step("Parking")
ok("/equipment/mount/park")
wait("parked", lambda: mount()["AtPark"], 600)

for device in ("camera", "mount"):
    ok(f"/equipment/{device}/disconnect")
print("PASS")
