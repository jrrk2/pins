#!/usr/bin/env bash
# Fetches and builds what the Origin test package needs from outside this repository, at the versions pinned in packaging/deps.env, so that a
# clone of this repository builds on any x86-64 Ubuntu 24.04 or later (or a derivative) that has the apt packages below. Run again, it does only
# what is missing. "make release" runs it with "build", "make simulator" with "sim".
#
#   build   the .NET SDK and Node (under .tools/ when the machine has no suitable one), the ninaAPI and Touch-N-Stars plugins, the Touch-N-Stars
#           frontend, and NINA/External (NOVAS and SOFA built from this repository, JPLEPH, an OpenCvSharpExtern built against the distribution's OpenCV)
#   sim     OriginSimulator, in artifacts/src/OriginSimulator, for make release-test and make test-sim, and a check for the astrometry.net index
#           files that Unpark's plate solve needs
#
# Usage: packaging/fetch-deps.sh [build] [sim]    (default: build)
set -Eeuo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT="$PWD"
# shellcheck source=deps.env
source packaging/deps.env

RUNTIME=linux-x64
TOOLS="$ROOT/.tools"
SRC=artifacts/src
TNS_DIR=NINA.Plugins/Touch-N-Stars/Touch-N-Stars
EXTERNAL="NINA/External"

BUILD_PACKAGES=(build-essential cmake git curl rsync patchelf dpkg-dev xz-utils python3 libopencv-dev libopencv-contrib-dev libraw-dev libcfitsio-dev
                libhidapi-dev)
SIM_PACKAGES=(qt6-base-dev libnova-dev libtiff-dev astrometry.net)

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
  echo "$oid  $target" | sha256sum -c --quiet - || fail "$pointer: the download does not match its checksum"
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
  local name="node-v$NODE_VERSION-linux-x64.tar.xz" tmp
  tmp="$(mktemp -d)"
  download "https://nodejs.org/dist/v$NODE_VERSION/$name" "$tmp/$name"
  download "https://nodejs.org/dist/v$NODE_VERSION/SHASUMS256.txt" "$tmp/SHASUMS256.txt"
  (cd "$tmp" && grep " $name\$" SHASUMS256.txt | sha256sum -c --quiet -) || fail "$name does not match its checksum"
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
  make -s -C NOVAS31 BUILDDIR="$build" TARGET="$build/libnovas_c.so" "$build/libnovas_c.so"
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
  cp -f "$build/libnovas_c.so" "$build/cio_ra.bin" "$EXTERNAL/$RUNTIME/NOVAS/"
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
  gcc -shared -o "$build/libsofa_c.so" "$build"/*.o -lm
  mkdir -p "$EXTERNAL/$RUNTIME/SOFA"
  cp -f "$build/libsofa_c.so" "$EXTERNAL/$RUNTIME/SOFA/"
}

build_external() {
  [[ -f "$EXTERNAL/JPLEPH" ]] || download "$JPLEPH_URL" "$EXTERNAL/JPLEPH"
  [[ -f "$EXTERNAL/LICENSE.txt" ]] || download "$EXTERNAL_LICENSE_URL" "$EXTERNAL/LICENSE.txt"
  [[ -f "$EXTERNAL/$RUNTIME/NOVAS/libnovas_c.so" && -f "$EXTERNAL/$RUNTIME/NOVAS/cio_ra.bin" ]] || build_novas
  [[ -f "$EXTERNAL/$RUNTIME/SOFA/libsofa_c.so" ]] || build_sofa
  if [[ ! -f "$EXTERNAL/$RUNTIME/libOpenCvSharpExtern.so" ]]; then
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
  for dir in "$HOME/astrometry_indices" /usr/share/astrometry /usr/local/share/astrometry; do
    for pattern in index-420[89] index-421[01] index-2mass-0[89] index-2mass-1[01]; do
      compgen -G "$dir/$pattern*.fits" > /dev/null && return
    done
  done
  fail "No astrometry.net index files for the Origin's field (4208-4211), without which Unpark cannot plate solve. Install them with:"$'\n' \
       " sudo apt-get install astrometry-data-2mass-08-19"$'\n'"or put the files in ~/astrometry_indices"
}

[[ $# -gt 0 ]] || set -- build
for what in "$@"; do
  case "$what" in
    build) fetch_build ;;
    sim) fetch_sim ;;
    *) fail "Unknown: $what (build or sim)" ;;
  esac
done
