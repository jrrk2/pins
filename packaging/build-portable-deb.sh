#!/usr/bin/env bash
# Builds a self-contained linux-x64 .deb of PINS with Touch-N-Stars, for trying on another computer.
#
# Unlike build-and-install-pins-x64.sh, which targets the machine it runs on, this installs to /opt/pins for any user and carries
# the OpenCV stack it was built against in /opt/pins/lib, so the target needs no particular OpenCV. It needs a glibc at least as
# new as the build machine's, and pulls in the handful of libraries in Depends.
#
# Expects:
#   NINA.Plugins/Touch-N-Stars and NINA.Plugins/ninaAPI built for Release/net10.0/linux-x64 (see BUILD_PINS.md),
#   with the frontend in NINA.Plugins/Touch-N-Stars/Touch-N-Stars/app
#   NINA/External/linux-x64 holding NOVAS, SOFA and an OpenCvSharpExtern built locally (see build_opencvsharp_extern in
#   build-and-install-pins-x64.sh)
#
# Usage: packaging/build-portable-deb.sh [iteration]    The result goes in artifacts/.
set -Eeuo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

ITERATION="${1:-1}"
RUNTIME=linux-x64
WORK=artifacts/portable-deb
PUBLISH="$WORK/publish"
ROOT="$WORK/root"
OPT="$ROOT/opt/pins"
EXTERN="NINA/External/$RUNTIME/libOpenCvSharpExtern.so"
TNS_DIR=NINA.Plugins/Touch-N-Stars/Touch-N-Stars
API_DIR=NINA.Plugins/ninaAPI/ninaAPI

fail() {
  echo "[ERROR] $*" >&2
  exit 1
}

for required in "$EXTERN" "NINA/External/$RUNTIME/NOVAS/libnovas_c.so" "NINA/External/$RUNTIME/SOFA/libsofa_c.so" "$TNS_DIR/app/index.html" \
  "$TNS_DIR/bin/Release/net10.0/$RUNTIME/TouchNStars.dll" "$API_DIR/bin/Release/net10.0/$RUNTIME/ninaAPI.dll"; do
  [[ -f "$required" ]] || fail "Missing $required"
done
for tool in dotnet dpkg-deb rsync patchelf ldd; do
  command -v "$tool" >/dev/null || fail "Missing $tool"
done
missing="$(env -u LD_LIBRARY_PATH ldd "$EXTERN" | grep 'not found' || true)"
[[ -z "$missing" ]] || fail "$EXTERN has unresolved libraries:"$'\n'"$missing"

rm -rf "$WORK"
mkdir -p "$PUBLISH" "$OPT/lib" "$OPT/plugins" "$ROOT/usr/bin" "$ROOT/DEBIAN"

echo "[INFO] Publishing PINS"
dotnet build System.Windows.Compat/System.Windows.Compat.csproj -c Release -v q
dotnet publish NINA/NINA.csproj -c Release -r "$RUNTIME" -o "$PUBLISH" -v q
rsync -a "$PUBLISH/" "$OPT/"
rsync -a "NINA/External/" "$OPT/External/"
cp -f System.Windows.Compat/bin/Release/net10.0/System.Windows.dll "$OPT/System.Windows.dll"

# The NuGet copy cannot load on current distributions. Probing tries OpenCvSharpExtern.so first, then this name.
cp -f "$EXTERN" "$OPT/libOpenCvSharpExtern.so"
cp -f "$EXTERN" "$OPT/OpenCvSharpExtern.so"
rm -f "$OPT/External/$RUNTIME/libOpenCvSharpExtern.so"

# Libraries the application opens by these unversioned names, which on a normal system only the -dev packages provide.
bundle_by_name() {
  local name="$1"
  local path
  path="$(ldconfig -p | awk -v n="$name" '$1 == n { print $NF; exit }')"
  [[ -n "$path" ]] || fail "$name not found; install its development package"
  cp -L "$path" "$OPT/External/$RUNTIME/$name"
  # Some are opened through LD_LIBRARY_PATH by this name, so the launcher's library folder needs it too.
  cp -L "$path" "$OPT/lib/$name"
  echo "$OPT/External/$RUNTIME/$name"
}

echo "[INFO] Bundling libraries"
sources=("$EXTERN" "$(bundle_by_name libraw.so)" "$(bundle_by_name libcfitsio.so)" "$(bundle_by_name libhidapi-hidraw.so)")

# glibc comes from the target, and must match its own loader. libssl keeps its security updates by coming from the target too.
is_target_library() {
  case "$1" in
    ld-linux*|libc.so*|libm.so*|libdl.so*|libpthread.so*|librt.so*|libresolv.so*|libnss_*|libutil.so*|libanl.so*) return 0 ;;
    libssl.so*|libcrypto.so*) return 0 ;;
    *) return 1 ;;
  esac
}

for source in "${sources[@]}"; do
  while read -r name _ path _; do
    [[ -n "${path:-}" && "$path" == /* ]] || continue
    is_target_library "$name" && continue
    [[ -e "$OPT/lib/$name" ]] || cp -L "$path" "$OPT/lib/$name"
  done < <(env -u LD_LIBRARY_PATH ldd "$source")
done
chmod -R u+w "$OPT/lib"
for library in "$OPT/lib"/* "$OPT/libOpenCvSharpExtern.so" "$OPT/OpenCvSharpExtern.so" "$OPT/External/$RUNTIME"/lib*.so; do
  patchelf --set-rpath '$ORIGIN:$ORIGIN/lib:$ORIGIN/../../lib' "$library" 2>/dev/null || true
done

echo "[INFO] Adding plugins"
exclude_list="$WORK/core-files.txt"
(cd "$PUBLISH" && find . -type f -printf '%P\n') > "$exclude_list"
printf '%s\n' '/??-??/' '/??/' >> "$exclude_list"
mkdir -p "$OPT/plugins/Touch-N-Stars" "$OPT/plugins/Advanced API"
rsync -a --exclude-from="$exclude_list" "$TNS_DIR/bin/Release/net10.0/$RUNTIME/" "$OPT/plugins/Touch-N-Stars/"
rsync -a --delete "$TNS_DIR/app/" "$OPT/plugins/Touch-N-Stars/app/"
rsync -a --exclude-from="$exclude_list" "$API_DIR/bin/Release/net10.0/$RUNTIME/" "$OPT/plugins/Advanced API/"

version="$(sed -n 's/.*AssemblyInformationalVersion("\([^"]*\)").*/\1/p' CommonAssemblyInfo.cs | head -n 1)"
[[ -n "$version" ]] || fail "Could not read the version"
package_version="$version+origin$ITERATION"
echo "$package_version" > "$OPT/plugins/.version"

cat > "$ROOT/usr/bin/pins" <<'LAUNCHER'
#!/bin/sh
# Starts PINS. Its plugins live in each user's data folder, so they are copied there when this version first runs.
set -e
app=/opt/pins
plugins="$HOME/.local/share/NINA/Plugins/3.0.0"
if ! cmp -s "$app/plugins/.version" "$plugins/.pins-version"; then
    mkdir -p "$plugins"
    chmod -R u+w "$plugins/Touch-N-Stars" "$plugins/Advanced API" 2>/dev/null || true
    cp -rf "$app/plugins/Touch-N-Stars" "$app/plugins/Advanced API" "$plugins/"
    chmod -R u+w "$plugins/Touch-N-Stars" "$plugins/Advanced API"     # plugins write next to themselves, as Advanced API's temp.png
    cp -f "$app/plugins/.version" "$plugins/.pins-version"
fi
echo "PI.N.S. is starting. Open http://localhost:5000 in a browser, or http://$(hostname -I 2>/dev/null | cut -d' ' -f1):5000 from another device. Stop it with Ctrl-C."
export LD_LIBRARY_PATH="$app/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
# Not the install folder, which this user cannot write to: PINS creates its INDI folder in the current directory.
cd "$HOME/.local/share/NINA"
exec "$app/NINA" "$@"
LAUNCHER
chmod 755 "$ROOT/usr/bin/pins"

chmod -R u+rwX,go+rX,go-w "$ROOT"
installed_size="$(du -sk "$ROOT" | cut -f1)"
cat > "$ROOT/DEBIAN/control" <<CONTROL
Package: pins-origin
Version: $package_version
Section: misc
Priority: optional
Architecture: amd64
Maintainer: PINS Team
Installed-Size: $installed_size
Depends: libc6 (>= $(ldd --version | sed -n '1s/.* \([0-9]*\.[0-9]*\).*/\1/p')), libssl3t64 | libssl3, libudev1, libusb-1.0-0
Recommends: astrometry-data-tycho2-07-littleendian | astrometry-data-tycho2-07
Conflicts: pins
Description: PI.N.S. with Touch-N-Stars and Celestron Origin drivers
 Self-contained test build. Run "pins" and open http://localhost:5000.
CONTROL

deb="artifacts/pins-origin_${package_version}_amd64.deb"
dpkg-deb --root-owner-group -Zxz --build "$ROOT" "$deb"
sha256sum "$deb" > "$deb.sha256"
du -sh "$OPT" "$deb"
echo "[INFO] Built $deb"
