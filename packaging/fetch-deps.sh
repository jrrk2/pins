#!/usr/bin/env bash
# Fetches and builds what the Origin test package needs from outside this repository, at the versions pinned in packaging/deps.env, so that a
# clone of this repository builds on any x86-64 or arm64 Ubuntu 24.04 or Debian 13 or later (or a derivative) that has the apt packages below, or on a Mac with the
# Homebrew packages below. Run again, it does only what is missing. "make release" runs it with "build", "make simulator" with "sim".
#
#   build   the .NET SDK and Node (under .tools/ when the machine has no suitable one), the ninaAPI and Touch-N-Stars plugins, the Touch-N-Stars
#           frontend, and NINA/External (NOVAS and SOFA built from this repository, JPLEPH, an OpenCvSharpExtern built against the distribution's OpenCV;
#           on macOS, against a static build of the pinned OpenCV, as Homebrew's is OpenCV 5)
#   sim     OriginSimulator, in artifacts/src/OriginSimulator, for make release-test and make test-sim, and a check for the astrometry.net index
#           files that Unpark's plate solve needs (in INDEX_DIR, or the usual places)
#
# Usage: packaging/fetch-deps.sh [build] [sim]    (default: build)
set -Eeuo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT="$PWD"
# shellcheck source=deps.env
source packaging/deps.env

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RUNTIME=osx-arm64 ;;
  Darwin-x86_64) RUNTIME=osx-x64 ;;
  *-aarch64|*-arm64) RUNTIME=linux-arm64 ;;
  *) RUNTIME=linux-x64 ;;
esac
if [[ "$RUNTIME" == osx-* ]]; then
  LIB=dylib
  SHA256SUM=(shasum -a 256)
else
  LIB=so
  SHA256SUM=(sha256sum)
fi
TOOLS="$ROOT/.tools"
SRC=artifacts/src
TNS_DIR=NINA.Plugins/Touch-N-Stars/Touch-N-Stars
EXTERNAL="NINA/External"

BUILD_PACKAGES=(build-essential cmake git curl rsync patchelf dpkg-dev xz-utils python3 libopencv-dev libopencv-contrib-dev libraw-dev libcfitsio-dev
                libhidapi-dev)
SIM_PACKAGES=(qt6-base-dev libnova-dev libtiff-dev astrometry.net)
if [[ "$RUNTIME" == osx-* ]]; then
  BUILD_PACKAGES=(cmake cfitsio libraw hidapi)
  SIM_PACKAGES=(qt libnova libtiff astrometry-net)
fi

log() {
  echo "[INFO] $*"
}

warn() {
  echo "[WARN] $*" >&2
}

fail() {
  echo "[ERROR] $*" >&2
  exit 1
}

check_packages() {
  local missing=() package
  if [[ "$RUNTIME" == osx-* ]]; then
    command -v brew > /dev/null || fail "Homebrew is needed: see https://brew.sh"
    for package in "$@"; do
      brew list --versions "$package" > /dev/null 2>&1 || missing+=("$package")
    done
    [[ ${#missing[@]} -eq 0 ]] || fail "Missing packages. Install them with:"$'\n'"  brew install ${missing[*]}"
    return
  fi
  for package in "$@"; do
    dpkg-query -W -f='${db:Status-Abbrev}' "$package" 2>/dev/null | grep -q '^ii' || missing+=("$package")
  done
  [[ ${#missing[@]} -eq 0 ]] || fail "Missing packages. Install them with:"$'\n'"  sudo apt-get install ${missing[*]}"
}

download() {
  local url="$1" target="$2"
  mkdir -p "$(dirname "$target")"
  curl -L --fail --retry 3 --retry-delay 5 -o "$target.part" "$url"
  if head -c 40 "$target.part" | grep -q '^version https://git-lfs'; then
    rm -f "$target.part"
    fail "$url is a Git LFS pointer, not the file"
  fi
  mv "$target.part" "$target"
}

# A Git LFS file of this repository, fetched over HTTPS from the LFS server of the "origin" remote, so that git-lfs need not be installed. The
# checked-out pointer is left as it is.
lfs_download() {
  local pointer="$1" target="$2"
  local oid size remote reply href
  oid="$(sed -n 's/^oid sha256:\([0-9a-f]*\)$/\1/p' "$pointer")"
  size="$(sed -n 's/^size \([0-9]*\)$/\1/p' "$pointer")"
  [[ -n "$oid" && -n "$size" ]] || fail "$pointer is not a Git LFS pointer"
  remote="$(git remote get-url origin | sed -e 's|^git@github.com:|https://github.com/|' -e 's|\.git$||')"
  reply="$(curl -s --fail --retry 3 -X POST -H 'Accept: application/vnd.git-lfs+json' -H 'Content-Type: application/vnd.git-lfs+json' \
    -d "{\"operation\":\"download\",\"transfers\":[\"basic\"],\"objects\":[{\"oid\":\"$oid\",\"size\":$size}]}" "$remote.git/info/lfs/objects/batch")" ||
    fail "The LFS server of $remote did not answer for $pointer"
  href="$(python3 -c 'import json, sys; print(json.load(sys.stdin)["objects"][0]["actions"]["download"]["href"])' <<< "$reply")" ||
    fail "The LFS server of $remote does not have $pointer: $reply"
  curl -L --fail --retry 3 -s -o "$target" "$href"
  echo "$oid  $target" | "${SHA256SUM[@]}" -c --quiet - || fail "$pointer: the download does not match its checksum"
}

# A clone of url at commit. A checkout already in dir is left alone, being perhaps someone's work, with a warning if it is not at the pin;
# with "owned", dir is one of this script's own and is moved to the pin.
clone_pinned() {
  local url="$1" commit="$2" dir="$3" owned="${4:-}"
  if [[ -e "$dir/.git" ]]; then
    local head
    head="$(git -C "$dir" rev-parse HEAD)"
    [[ "$head" == "$commit" ]] && return
    if [[ "$owned" == owned ]]; then
      log "Moving $dir to ${commit:0:10}"
      git -C "$dir" fetch -q --depth 1 origin "$commit"
      git -C "$dir" checkout -q --detach FETCH_HEAD
      git -C "$dir" submodule update -q --init --depth 1
    elif git -C "$dir" merge-base --is-ancestor "$commit" HEAD 2>/dev/null; then
      warn "$dir is at ${head:0:10}, ahead of the pinned ${commit:0:10}: building what is there"
    else
      warn "$dir is at ${head:0:10}, not the pinned ${commit:0:10}: building what is there"
    fi
    return
  fi
  [[ ! -e "$dir" || -z "$(ls -A "$dir")" ]] || fail "$dir exists but is not a git checkout; move it away"
  log "Fetching $url at ${commit:0:10} into $dir"
  git init -q "$dir"
  git -C "$dir" remote add origin "$url"
  git -C "$dir" fetch -q --depth 1 origin "$commit"
  git -C "$dir" checkout -q --detach FETCH_HEAD
  git -C "$dir" submodule update -q --init --depth 1
}

ensure_dotnet() {
  # global.json takes any SDK of its feature band (rollForward latestPatch), as 10.0.4xx for 10.0.400
  local wanted band
  wanted="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json)"
  band="${wanted%??}"
  if [[ -x "$TOOLS/dotnet/dotnet" ]] && "$TOOLS/dotnet/dotnet" --list-sdks | grep -q "^${band//./\\.}"; then
    export PATH="$TOOLS/dotnet:$PATH" DOTNET_ROOT="$TOOLS/dotnet"
  elif command -v dotnet >/dev/null && dotnet --list-sdks | grep -q "^${band//./\\.}"; then
    return
  else
    log "Installing the .NET SDK $DOTNET_SDK_VERSION into .tools/dotnet"
    download https://dot.net/v1/dotnet-install.sh "$TOOLS/dotnet-install.sh"
    bash "$TOOLS/dotnet-install.sh" --version "$DOTNET_SDK_VERSION" --install-dir "$TOOLS/dotnet" --no-path
    export PATH="$TOOLS/dotnet:$PATH" DOTNET_ROOT="$TOOLS/dotnet"
  fi
}

ensure_node() {
  local major="${NODE_VERSION%%.*}" have
  if [[ -x "$TOOLS/node/bin/node" ]]; then
    export PATH="$TOOLS/node/bin:$PATH"
    return
  fi
  have="$(node --version 2>/dev/null | sed 's/^v\([0-9]*\).*/\1/' || true)"
  [[ -n "$have" && "$have" -ge "$major" ]] && return
  log "Installing Node $NODE_VERSION into .tools/node"
  local platform="$RUNTIME" tmp name
  [[ "$RUNTIME" == osx-* ]] && platform="darwin-${RUNTIME#osx-}"
  name="node-v$NODE_VERSION-$platform.tar.xz"
  tmp="$(mktemp -d)"
  download "https://nodejs.org/dist/v$NODE_VERSION/$name" "$tmp/$name"
  download "https://nodejs.org/dist/v$NODE_VERSION/SHASUMS256.txt" "$tmp/SHASUMS256.txt"
  (cd "$tmp" && grep " $name\$" SHASUMS256.txt | "${SHA256SUM[@]}" -c --quiet -) || fail "$name does not match its checksum"
  mkdir -p "$TOOLS/node"
  tar -xJf "$tmp/$name" -C "$TOOLS/node" --strip-components=1
  rm -rf "$tmp"
  export PATH="$TOOLS/node/bin:$PATH"
}

# Built here, it is rebuilt when the pin moves; one already there from elsewhere is left alone.
build_frontend() {
  local stamp="$SRC/touch-n-stars.commit"
  if [[ -f "$TNS_DIR/app/index.html" ]]; then
    if [[ ! -f "$stamp" ]]; then
      warn "$TNS_DIR/app was not built by this script: building what is there"
      return
    fi
    [[ "$(cat "$stamp")" == "$TNS_APP_COMMIT" ]] && return
  fi
  ensure_node
  clone_pinned "$TNS_APP_URL" "$TNS_APP_COMMIT" "$SRC/touch-n-stars" owned
  log "Building the Touch-N-Stars frontend with Node $(node --version)"
  (cd "$SRC/touch-n-stars" && npm ci --no-audit --no-fund && npm run build)
  rsync -a --delete "$SRC/touch-n-stars/dist/" "$TNS_DIR/app/"
  echo "$TNS_APP_COMMIT" > "$stamp"
}

build_novas() {
  log "Building NOVAS"
  local build="$ROOT/$SRC/novas-build" cio=NOVAS31/NOVAS31/CIO_RA.TXT
  rm -rf "$build"
  mkdir -p "$build"
  # in a folder of its own: the Makefile otherwise builds into NOVAS31/, which is not ignored
  make -s -C NOVAS31 BUILDDIR="$build" TARGET="$build/libnovas_c.$LIB" "$build/libnovas_c.$LIB"
  # cio_file never stops writing when CIO_RA.TXT is a Git LFS pointer: it fills the disk
  if grep -q '^version https://git-lfs' "$cio"; then
    lfs_download "$cio" "$build/CIO_RA.TXT"
  else
    cp "$cio" "$build/CIO_RA.TXT"
  fi
  gcc -O2 -std=c99 -o "$build/cio_file" NOVAS31/NOVAS31/cio_file.c -lm
  (cd "$build" && ./cio_file > /dev/null)
  [[ -s "$build/cio_ra.bin" ]] || fail "cio_file did not make cio_ra.bin"
  mkdir -p "$EXTERNAL/$RUNTIME/NOVAS"
  cp -f "$build/libnovas_c.$LIB" "$build/cio_ra.bin" "$EXTERNAL/$RUNTIME/NOVAS/"
}

build_sofa() {
  log "Building SOFA"
  local build="$ROOT/$SRC/sofa-build" source
  rm -rf "$build"
  mkdir -p "$build"
  for source in "$ROOT"/SOFA/SOFA/src/*.c; do
    [[ "$(basename "$source")" == t_sofa_c.c ]] && continue    # the test program
    (cd "$build" && gcc -fPIC -O2 -c "$source")
  done
  gcc -shared -o "$build/libsofa_c.$LIB" "$build"/*.o -lm
  mkdir -p "$EXTERNAL/$RUNTIME/SOFA"
  cp -f "$build/libsofa_c.$LIB" "$EXTERNAL/$RUNTIME/SOFA/"
}

# OpenCvSharp's version, from the OpenCvSharp4 package in NINA/NINA.csproj: its tag, and the OpenCV it was made for (4.11.0.20250507: 4.11.0)
opencvsharp_tag() {
  sed -n 's/.*Include="OpenCvSharp4" Version="\([^"]*\)".*/\1/p' NINA/NINA.csproj | head -n 1
}

# Homebrew's OpenCV is 5, which OpenCvSharp 4 does not build against; OpenCvSharp makes no macOS runtime package. So OpenCV is built here, static,
# with the settings of OpenCvSharp's own (disabled) macOS workflow, and with its own image libraries rather than Homebrew's or MacPorts': the
# OpenCvSharpExtern that results needs nothing but the system's.
build_opencvsharp_extern_macos() {
  local tag opencv build="$ROOT/$SRC/opencv-macos" jobs
  tag="$(opencvsharp_tag)"
  [[ -n "$tag" ]] || fail "Could not read the OpenCvSharp4 version from NINA/NINA.csproj"
  opencv="${tag%.*}"
  jobs="$(sysctl -n hw.ncpu)"
  local clean=(env -u CPATH -u LIBRARY_PATH -u PKG_CONFIG_PATH -u DYLD_LIBRARY_PATH -u DYLD_FALLBACK_LIBRARY_PATH)
  local ignore="-DCMAKE_IGNORE_PREFIX_PATH=/opt/homebrew;/opt/local;/usr/local"
  if [[ ! -f "$build/install/lib/cmake/opencv4/OpenCVConfig.cmake" ]]; then
    log "Building OpenCV $opencv, static, for OpenCvSharp $tag (a few minutes)"
    rm -rf "$build"
    git clone -q --depth 1 --branch "$opencv" https://github.com/opencv/opencv "$build/opencv"
    git clone -q --depth 1 --branch "$opencv" https://github.com/opencv/opencv_contrib "$build/opencv_contrib"
    "${clean[@]}" cmake -S "$build/opencv" -B "$build/build" -G "Unix Makefiles" "$ignore" \
      -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$build/install" -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
      -DOPENCV_EXTRA_MODULES_PATH="$build/opencv_contrib/modules" -DBUILD_SHARED_LIBS=OFF -DOPENCV_ENABLE_NONFREE=ON \
      -DBUILD_TESTS=OFF -DBUILD_PERF_TESTS=OFF -DBUILD_DOCS=OFF -DBUILD_EXAMPLES=OFF -DBUILD_JAVA=OFF -DBUILD_opencv_apps=OFF \
      -DBUILD_opencv_java_bindings_generator=OFF -DBUILD_opencv_python_bindings_generator=OFF -DBUILD_opencv_python_tests=OFF \
      -DBUILD_opencv_objc_bindings_generator=OFF -DBUILD_opencv_js=OFF -DBUILD_opencv_js_bindings_generator=OFF -DBUILD_opencv_ts=OFF \
      -DBUILD_opencv_barcode=OFF -DBUILD_opencv_bioinspired=OFF -DBUILD_opencv_ccalib=OFF -DBUILD_opencv_datasets=OFF \
      -DBUILD_opencv_dnn_objdetect=OFF -DBUILD_opencv_dpm=OFF -DBUILD_opencv_fuzzy=OFF -DBUILD_opencv_gapi=OFF \
      -DBUILD_opencv_intensity_transform=OFF -DBUILD_opencv_mcc=OFF -DBUILD_opencv_rapid=OFF -DBUILD_opencv_reg=OFF \
      -DBUILD_opencv_stereo=OFF -DBUILD_opencv_structured_light=OFF -DBUILD_opencv_surface_matching=OFF -DBUILD_opencv_videostab=OFF \
      -DBUILD_opencv_viz=OFF -DBUILD_opencv_hdf=OFF -DBUILD_opencv_freetype=OFF -DBUILD_opencv_sfm=OFF -DBUILD_opencv_cvv=OFF \
      -DWITH_FFMPEG=OFF -DWITH_GSTREAMER=OFF -DWITH_EIGEN=OFF -DWITH_ADE=OFF -DWITH_OPENEXR=OFF -DWITH_QT=OFF -DWITH_VTK=OFF \
      -DWITH_TESSERACT=OFF -DWITH_HDF5=OFF -DWITH_FREETYPE=OFF -DWITH_HARFBUZZ=OFF -DWITH_OPENJPEG=OFF -DWITH_JASPER=OFF \
      -DWITH_IPP=OFF -DWITH_OPENCL=OFF -DWITH_PROTOBUF=ON -DBUILD_PROTOBUF=ON \
      -DBUILD_ZLIB=ON -DBUILD_JPEG=ON -DBUILD_PNG=ON -DBUILD_TIFF=ON -DBUILD_WEBP=ON -DWITH_AVIF=OFF -DWITH_SPNG=OFF > "$build/cmake.log"
    "${clean[@]}" cmake --build "$build/build" --parallel "$jobs" > "$build/build.log" 2>&1 || fail "OpenCV did not build; see $build/build.log"
    cmake --install "$build/build" > /dev/null
    rm -rf "$build/build"    # gigabytes; the installed libraries are what OpenCvSharpExtern needs
  fi

  log "Building OpenCvSharpExtern $tag against it"
  local sharp="$ROOT/$SRC/opencvsharp"
  rm -rf "$sharp"
  git clone -q --depth 1 --branch "$tag" https://github.com/shimat/opencvsharp "$sharp"
  sed -i.orig 's/-std=c++11/-std=c++17/' "$sharp/src/CMakeLists.txt"
  "${clean[@]}" cmake -S "$sharp/src" -B "$sharp/build" "$ignore" -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DOpenCV_DIR="$build/install/lib/cmake/opencv4" > "$sharp/cmake.log"
  "${clean[@]}" cmake --build "$sharp/build" --parallel "$jobs" > "$sharp/build.log" 2>&1 || fail "OpenCvSharpExtern did not build; see $sharp/build.log"
  local built="$sharp/build/OpenCvSharpExtern/libOpenCvSharpExtern.dylib" linked
  [[ -f "$built" ]] || fail "OpenCvSharpExtern build did not produce $built"
  linked="$(otool -L "$built" | tail -n +2)"
  if grep -vE '^\s*(/usr/lib/|/System/)' <<< "$linked" | grep -v OpenCvSharpExtern | grep -q .; then
    fail "OpenCvSharpExtern links libraries outside the system:"$'\n'"$linked"
  fi
  # Read in full first: grep -q closing a pipe early counts as a failure under pipefail.
  local exported
  exported="$(nm -gU "$built")"
  grep -q ' _core_Mat_new2$' <<< "$exported" || fail "OpenCvSharpExtern does not export the expected entry points"
  mkdir -p "$EXTERNAL/$RUNTIME"
  cp -f "$built" "$EXTERNAL/$RUNTIME/libOpenCvSharpExtern.dylib"
  log "Staged OpenCvSharpExtern at: $EXTERNAL/$RUNTIME/libOpenCvSharpExtern.dylib"
}

build_external() {
  [[ -f "$EXTERNAL/JPLEPH" ]] || download "$JPLEPH_URL" "$EXTERNAL/JPLEPH"
  [[ -f "$EXTERNAL/LICENSE.txt" ]] || download "$EXTERNAL_LICENSE_URL" "$EXTERNAL/LICENSE.txt"
  [[ -f "$EXTERNAL/$RUNTIME/NOVAS/libnovas_c.$LIB" && -f "$EXTERNAL/$RUNTIME/NOVAS/cio_ra.bin" ]] || build_novas
  [[ -f "$EXTERNAL/$RUNTIME/SOFA/libsofa_c.$LIB" ]] || build_sofa
  if [[ "$RUNTIME" == osx-* ]]; then
    [[ -f "$EXTERNAL/$RUNTIME/libOpenCvSharpExtern.dylib" ]] || build_opencvsharp_extern_macos
  elif [[ ! -f "$EXTERNAL/$RUNTIME/libOpenCvSharpExtern.so" ]]; then
    # in a subshell: the install script sets its own shell options and IFS
    (TARGET_RUNTIME="$RUNTIME" && source ./build-and-install-pins-x64.sh && build_opencvsharp_extern)
  fi
}

fetch_build() {
  check_packages "${BUILD_PACKAGES[@]}"
  ensure_dotnet
  clone_pinned "$NINAAPI_URL" "$NINAAPI_COMMIT" NINA.Plugins/ninaAPI
  clone_pinned "$TNS_PLUGIN_URL" "$TNS_PLUGIN_COMMIT" NINA.Plugins/Touch-N-Stars
  build_frontend
  build_external
  log "Build dependencies in place (.NET SDK $(dotnet --version))"
}

fetch_sim() {
  check_packages "${SIM_PACKAGES[@]}"
  clone_pinned "$SIM_URL" "$SIM_COMMIT" "$SRC/OriginSimulator" owned
  # As astrometry.net publishes them (index-4208-00.fits), or as Debian's astrometry-data-2mass packages name them (index-2mass-08.fits)
  local dir pattern
  for dir in ${INDEX_DIR:+"$INDEX_DIR"} "$HOME/astrometry_indices" /usr/share/astrometry /usr/local/share/astrometry /opt/homebrew/share/astrometry /opt/local/share/astrometry; do
    for pattern in index-420[89] index-421[01] index-2mass-0[89] index-2mass-1[01]; do
      compgen -G "$dir/$pattern*.fits" > /dev/null && return
    done
  done
  local install=" sudo apt-get install astrometry-data-2mass-08-19"$'\n'"or put the files in ~/astrometry_indices"
  [[ "$RUNTIME" == osx-* ]] && install=" mkdir -p ~/astrometry_indices && cd ~/astrometry_indices && for i in 08 09 10 11; do curl -fLO https://data.astrometry.net/4200/index-42\$i.fits; done"
  fail "No astrometry.net index files for the Origin's field (4208-4211), without which Unpark cannot plate solve. Install them with:"$'\n'"$install"
}

[[ $# -gt 0 ]] || set -- build
for what in "$@"; do
  case "$what" in
    build) fetch_build ;;
    sim) fetch_sim ;;
    *) fail "Unknown: $what (build or sim)" ;;
  esac
done
