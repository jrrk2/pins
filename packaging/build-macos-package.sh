#!/usr/bin/env bash
# Builds a self-contained macOS package of PINS with Touch-N-Stars: a .tar.gz holding a pins/ folder, to unpack anywhere and start with pins/pins.
# The macOS counterpart of build-portable-deb.sh, run on a Mac.
#
# The folder carries OpenCvSharpExtern (OpenCV linked in statically), NOVAS, SOFA, and Homebrew's cfitsio and libraw with the libraries they need,
# so the Mac it runs on needs only astrometry.net (brew install astrometry-net) and the index files for plate solving. It runs on the macOS it was
# built on and later ones. Everything is signed ad hoc, as Apple Silicon requires; nothing is notarised, so a copy downloaded with a browser needs
# its quarantine attribute removed (xattr -dr com.apple.quarantine pins).
#
# Expects what packaging/fetch-deps.sh build makes: NINA/External/osx-<arch> holding NOVAS, SOFA and OpenCvSharpExtern, and the Touch-N-Stars and
# ninaAPI plugins built for Release/net10.0/osx-<arch>, with the frontend in NINA.Plugins/Touch-N-Stars/Touch-N-Stars/app.
#
# Usage: packaging/build-macos-package.sh [iteration]    The result goes in artifacts/.
set -Eeuo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

ITERATION="${1:-1}"
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RUNTIME=osx-arm64 ;;
  Darwin-x86_64) RUNTIME=osx-x64 ;;
  *) echo "[ERROR] This builds the macOS package, on a Mac" >&2; exit 1 ;;
esac
WORK=artifacts/macos-package
PUBLISH="$WORK/publish"
PINS="$WORK/pins"
APP="$PINS/app"
EXTERNAL="NINA/External/$RUNTIME"
EXTERN="$EXTERNAL/libOpenCvSharpExtern.dylib"
TNS_DIR=NINA.Plugins/Touch-N-Stars/Touch-N-Stars
API_DIR=NINA.Plugins/ninaAPI/ninaAPI

fail() {
  echo "[ERROR] $*" >&2
  exit 1
}

for required in "$EXTERN" "$EXTERNAL/NOVAS/libnovas_c.dylib" "$EXTERNAL/SOFA/libsofa_c.dylib" "$TNS_DIR/app/index.html" \
  "$TNS_DIR/bin/Release/net10.0/$RUNTIME/TouchNStars.dll" "$API_DIR/bin/Release/net10.0/$RUNTIME/ninaAPI.dll"; do
  [[ -f "$required" ]] || fail "Missing $required"
done
for tool in dotnet rsync otool install_name_tool codesign brew; do
  command -v "$tool" >/dev/null || fail "Missing $tool"
done

rm -rf "$WORK"
mkdir -p "$PUBLISH" "$APP/lib" "$PINS/plugins"

echo "[INFO] Publishing PINS"
dotnet build System.Windows.Compat/System.Windows.Compat.csproj -c Release -v q
dotnet publish NINA/NINA.csproj -c Release -r "$RUNTIME" -o "$PUBLISH" -v q
rsync -a "$PUBLISH/" "$APP/"
rsync -a "NINA/External/" "$APP/External/" --exclude 'linux-*' --exclude 'win-*'
cp -f System.Windows.Compat/bin/Release/net10.0/System.Windows.dll "$APP/System.Windows.dll"
# OpenCvSharp finds it next to the application
mv "$APP/External/$RUNTIME/libOpenCvSharpExtern.dylib" "$APP/libOpenCvSharpExtern.dylib"

# Homebrew's libraries, and those they link from outside the system, copied into app/lib and pointed at each other there. A Homebrew library
# names its dependencies by absolute path (/opt/homebrew/opt/...), or by @rpath or @loader_path relative to its own folder.
is_system_library() {
  [[ "$1" == /usr/lib/* || "$1" == /System/* ]]
}

resolve_dependency() {
  local dependency="$1" from="$2"
  case "$dependency" in
    @loader_path/*) echo "$(dirname "$from")/${dependency#@loader_path/}" ;;
    @rpath/*)
      local rpath
      while read -r rpath; do
        rpath="${rpath/@loader_path/$(dirname "$from")}"
        [[ -f "$rpath/${dependency#@rpath/}" ]] && { echo "$rpath/${dependency#@rpath/}"; return; }
      done < <(otool -l "$from" | awk '$1 == "path" { print $2 }')
      fail "$from: cannot find $dependency" ;;
    *) echo "$dependency" ;;
  esac
}

# Copies a library into app/lib (with what it needs, recursively) and rewrites its references to @rpath, which app/lib is on.
bundle_library() {
  local source="$1" target="$2" name dependency path
  source="$(realpath "$source")"
  cp -f "$source" "$target"
  chmod u+w "$target"
  install_name_tool -id "@rpath/$(basename "$target")" "$target" 2>/dev/null
  while read -r dependency; do
    path="$(resolve_dependency "$dependency" "$source")"
    is_system_library "$path" && continue
    name="$(basename "$path")"
    [[ -f "$APP/lib/$name" ]] || bundle_library "$path" "$APP/lib/$name"
    install_name_tool -change "$dependency" "@rpath/$name" "$target" 2>/dev/null
  done < <(otool -L "$source" | tail -n +2 | awk '{ print $1 }' | grep -v "^@rpath/$(basename "$source")\$" | grep -vx "$source")
}

# The libraries the application opens by name: in the folders DllLoader looks in, with app/lib on their search path.
echo "[INFO] Bundling libraries"
bundle_from_brew() {
  local formula="$1" name="$2" folder="$3" source
  source="$(brew --prefix "$formula")/lib/$name"
  [[ -f "$source" ]] || fail "$source not found: brew install $formula"
  mkdir -p "$APP/External/$RUNTIME/$folder"
  bundle_library "$source" "$APP/External/$RUNTIME/$folder/$name"
  install_name_tool -add_rpath "@loader_path/../../../lib" "$APP/External/$RUNTIME/$folder/$name"
}
bundle_from_brew cfitsio libcfitsio.dylib Cfitsio
bundle_from_brew libraw libraw.dylib Libraw
for library in "$APP"/lib/*.dylib; do
  install_name_tool -add_rpath "@loader_path" "$library" 2>/dev/null || true
done

echo "[INFO] Adding plugins"
exclude_list="$WORK/core-files.txt"
(cd "$PUBLISH" && find . -type f | sed 's|^\./||') > "$exclude_list"
printf '%s\n' '/??-??/' '/??/' >> "$exclude_list"
mkdir -p "$PINS/plugins/Touch-N-Stars" "$PINS/plugins/Advanced API"
rsync -a --exclude-from="$exclude_list" "$TNS_DIR/bin/Release/net10.0/$RUNTIME/" "$PINS/plugins/Touch-N-Stars/"
rsync -a --delete "$TNS_DIR/app/" "$PINS/plugins/Touch-N-Stars/app/"
rsync -a --exclude-from="$exclude_list" "$API_DIR/bin/Release/net10.0/$RUNTIME/" "$PINS/plugins/Advanced API/"

version="$(sed -n 's/.*AssemblyInformationalVersion("\([^"]*\)").*/\1/p' CommonAssemblyInfo.cs | head -n 1)"
[[ -n "$version" ]] || fail "Could not read the version"
package_version="$version+origin$ITERATION"
echo "$package_version" > "$PINS/plugins/.version"

cat > "$PINS/pins" <<'LAUNCHER'
#!/bin/bash
# Starts PINS. Its plugins live in each user's data folder, so they are copied there when this version first runs.
set -e
here="$(dirname "$(realpath "$0")")"
app="$here/app"
data="$HOME/Library/Application Support/NINA"
plugins="$data/Plugins/3.0.0"
if ! cmp -s "$here/plugins/.version" "$plugins/.pins-version"; then
    mkdir -p "$plugins"
    chmod -R u+w "$plugins/Touch-N-Stars" "$plugins/Advanced API" 2>/dev/null || true
    rm -rf "$plugins/Touch-N-Stars" "$plugins/Advanced API"
    cp -R "$here/plugins/Touch-N-Stars" "$here/plugins/Advanced API" "$plugins/"
    chmod -R u+w "$plugins/Touch-N-Stars" "$plugins/Advanced API"     # plugins write next to themselves, as Advanced API's temp.png
    cp -f "$here/plugins/.version" "$plugins/.pins-version"
fi
# Touch-N-Stars takes the first free port from 5000, which macOS's AirPlay Receiver usually holds
port=5000
while lsof -nP -iTCP:$port -sTCP:LISTEN > /dev/null 2>&1; do
    port=$((port + 1))
done
if [ $port != 5000 ]; then
    echo "Port 5000 is in use (on a Mac, by AirPlay Receiver: System Settings, General, AirDrop & Handoff), so Touch-N-Stars will be on port $port."
fi
address="$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true)"
echo "PI.N.S. is starting. Open http://localhost:$port in a browser${address:+, or http://$address:$port from another device}. Stop it with Ctrl-C."
# astrometry.net's solve-field, from Homebrew or MacPorts, for plate solving
export PATH="$PATH:/opt/homebrew/bin:/usr/local/bin:/opt/local/bin"
# Not the application's folder, which may not be writable: PINS creates its INDI folder in the current directory.
cd "$data"
exec "$app/NINA" "$@"
LAUNCHER
chmod 755 "$PINS/pins"

# NOVAS, SOFA and OpenCvSharpExtern are named by the folder they were built in; nothing loads them by that name, but it should not ship
while read -r library; do
  install_name_tool -id "@rpath/$(basename "$library")" "$library" 2>/dev/null
done < <(find "$APP" -name '*.dylib')

# Apple Silicon runs only signed code, and install_name_tool has invalidated the signatures it had: sign everything again, ad hoc.
echo "[INFO] Signing"
while read -r file; do
  codesign --force --sign - "$file" 2>/dev/null || fail "Could not sign $file"
done < <(find "$APP" "$PINS/plugins" -type f \( -name '*.dylib' -o -perm -u+x \) ! -name '*.dll' ! -name '*.json' ! -name '*.pdb' | while read -r f; do
  file -b "$f" | grep -q '^Mach-O' && echo "$f"; done)

# Each library must load from the folder, not from Homebrew
while read -r file; do
  while read -r dependency; do
    [[ "$dependency" == /usr/lib/* || "$dependency" == /System/* || "$dependency" == @rpath/* || "$dependency" == @loader_path/* || "$dependency" == @executable_path/* ]] ||
      fail "$file still links $dependency"
  done < <(otool -L "$file" | tail -n +2 | awk '{ print $1 }')
done < <(find "$APP/lib" "$APP/External/$RUNTIME" "$APP/libOpenCvSharpExtern.dylib" -name '*.dylib')

package="artifacts/pins-origin_${package_version}_macos-${RUNTIME#osx-}.tar.gz"
tar -czf "$package" -C "$WORK" pins
(cd "$(dirname "$package")" && shasum -a 256 "$(basename "$package")" > "$(basename "$package").sha256")
du -sh "$PINS" "$package"
echo "[INFO] Built $package"
