#!/usr/bin/env bash
# Builds a Raspberry Pi image of PI.N.S. for Raspberry Pi Imager ("Use custom"): Raspberry Pi OS Lite, pinned in packaging/deps.env, with
#   - the arm64 pins-origin package, which the first boot installs with apt (Debian's OpenCV and the rest come from the internet then)
#     and starts as the user made in Imager (pins-firstboot.service, then pins@<user>.service)
#   - the astrometry.net index files for the Origin's field (4208-4211) in /usr/share/astrometry
#   - the Ethernet port in NetworkManager's shared mode, which serves the Origin DHCP
#   - a polkit rule letting the user scan for Wi-Fi over SSH
# Imager's own settings (hostname, user, Wi-Fi, SSH key, locale) apply as to Raspberry Pi OS itself.
#
# Needs no root: the root filesystem is edited in place with debugfs, so it keeps everything else as Raspberry Pi built it (owners, modes,
# file capabilities). Each file added is made root's, with its mode set here.
#
# Usage: packaging/build-pi-image.sh <pins-origin_*_arm64.deb> [index-dir]
#   index-dir holds index-4208..4211 (default: downloaded into artifacts/pi-image/indices)
#   The result is artifacts/pins-origin_<version>_rpi-arm64.img.xz, with its .sha256.
set -Eeuo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
# shellcheck source=deps.env
source packaging/deps.env
export PATH="$PATH:/usr/sbin:/sbin"

DEB="${1:?Usage: $0 <pins-origin_*_arm64.deb> [index-dir]}"
WORK=artifacts/pi-image
INDICES="${2:-$WORK/indices}"
GROW_MB=1024        # more room in the root filesystem; the first boot grows it to the card anyway

fail() {
  echo "[ERROR] $*" >&2
  exit 1
}

[[ -f "$DEB" ]] || fail "$DEB not found: build it on a Raspberry Pi (make release there, see BUILD_PINS.md) and copy it here, or name another with DEB=..."
[[ "$(dpkg-deb -f "$DEB" Architecture)" == arm64 ]] || fail "$DEB is not an arm64 package"
for tool in curl xz sfdisk debugfs e2fsck resize2fs dpkg-deb; do
  command -v "$tool" >/dev/null || fail "Missing $tool"
done
mkdir -p "$WORK"

base_xz="$WORK/$(basename "$PI_OS_IMAGE_URL")"
if [[ ! -f "$base_xz" ]] || ! echo "$PI_OS_IMAGE_SHA256  $base_xz" | sha256sum -c --quiet - 2>/dev/null; then
  echo "[INFO] Downloading $(basename "$PI_OS_IMAGE_URL")"
  curl -fL --retry 3 -o "$base_xz.part" "$PI_OS_IMAGE_URL"
  echo "$PI_OS_IMAGE_SHA256  $base_xz.part" | sha256sum -c --quiet - || fail "$PI_OS_IMAGE_URL does not match its checksum"
  mv "$base_xz.part" "$base_xz"
fi

if [[ "$INDICES" == "$WORK/indices" ]]; then
  mkdir -p "$INDICES"
  for i in 08 09 10 11; do
    [[ -s "$INDICES/index-42$i.fits" ]] || curl -fL --retry 3 -o "$INDICES/index-42$i.fits" "https://data.astrometry.net/4200/index-42$i.fits"
  done
fi
for i in 08 09 10 11; do
  [[ -s "$INDICES/index-42$i.fits" ]] || fail "$INDICES/index-42$i.fits missing"
done

version="$(dpkg-deb -f "$DEB" Version)"
image="$WORK/pins-origin_${version}_rpi-arm64.img"
echo "[INFO] Unpacking Raspberry Pi OS"
xz -dc "$base_xz" > "$image"

# The root filesystem is the second partition: grown at its end, edited as a file of its own, and written back
read -r start size < <(sfdisk -d "$image" | awk -F'[=,]' '/start=/ && ++n == 2 { gsub(/ /, ""); print $2, $4 }')
[[ -n "$start" && -n "$size" ]] || fail "Could not read the root partition of $image"
truncate -s "+${GROW_MB}M" "$image"
echo ", +" | sfdisk -q -N 2 "$image"
size=$(( size + GROW_MB * 2048 ))
root="$WORK/root.ext4"
dd if="$image" of="$root" bs=512 skip="$start" count=$(( size - GROW_MB * 2048 )) status=none
truncate -s $(( size * 512 )) "$root"
e2fsck -fy "$root" > /dev/null || [[ $? -le 1 ]] || fail "The root filesystem of $(basename "$PI_OS_IMAGE_URL") has errors"
resize2fs -f "$root" > /dev/null

echo "[INFO] Adding PI.N.S."
commands="$WORK/debugfs.txt"
: > "$commands"
# add <source> <path> <mode>: a root-owned file
add() {
  printf 'write %s %s\nsif %s uid 0\nsif %s gid 0\nsif %s mode 0100%s\n' "$1" "$2" "$2" "$2" "$2" "$3" >> "$commands"
}
# dir <path>: a root-owned directory, 755
dir() {
  printf 'mkdir %s\nsif %s uid 0\nsif %s gid 0\nsif %s mode 040755\n' "$1" "$1" "$1" "$1" >> "$commands"
}
dir /opt/pins-image
add "$DEB" "/opt/pins-image/$(basename "$DEB")" 644
add packaging/pi-image/pins-firstboot.sh /opt/pins-image/pins-firstboot.sh 755
add packaging/pi-image/pins-firstboot.service /etc/systemd/system/pins-firstboot.service 644
add packaging/pi-image/pins@.service /etc/systemd/system/pins@.service 644
echo "symlink /etc/systemd/system/multi-user.target.wants/pins-firstboot.service /etc/systemd/system/pins-firstboot.service" >> "$commands"
add packaging/pi-image/origin-ethernet.nmconnection /etc/NetworkManager/system-connections/origin-ethernet.nmconnection 600
add packaging/pi-image/50-pins-wifi-scan.rules /etc/polkit-1/rules.d/50-pins-wifi-scan.rules 644
dir /usr/share/astrometry
for i in 08 09 10 11; do
  add "$INDICES/index-42$i.fits" "/usr/share/astrometry/index-42$i.fits" 644
done
debugfs -w -f "$commands" "$root" > "$WORK/debugfs.log" 2>&1
if grep -E "^debugfs: .*|Could not|not found|already exists|Ext2 .* error" "$WORK/debugfs.log" | grep -v "^debugfs [0-9]" | grep -vE "^debugfs: (write|sif|mkdir|symlink) " | grep -q .; then
  cat "$WORK/debugfs.log" >&2
  fail "debugfs reported errors"
fi
e2fsck -fy "$root" > /dev/null || [[ $? -le 1 ]] || fail "The root filesystem has errors after the changes"

# What was added reads back as it should
check() {
  local listed
  listed="$(debugfs -R "stat $1" "$root" 2>/dev/null | sed -n 's/^Inode: .*Mode:  *\([0-7]*\).*/\1/p')"
  [[ "$listed" == "$2" ]] || fail "$1 has mode '$listed', not $2"
  debugfs -R "stat $1" "$root" 2>/dev/null | grep -q "^User: *0 *Group: *0" || fail "$1 is not root's"
}
check "/opt/pins-image/$(basename "$DEB")" 0644
check /opt/pins-image/pins-firstboot.sh 0755
check /etc/NetworkManager/system-connections/origin-ethernet.nmconnection 0600
check /usr/share/astrometry/index-4208.fits 0644
debugfs -R "stat /etc/systemd/system/multi-user.target.wants/pins-firstboot.service" "$root" 2>/dev/null | grep -q "Type: symlink" ||
  fail "pins-firstboot.service is not enabled"

dd if="$root" of="$image" bs=512 seek="$start" conv=notrunc status=none
rm -f "$root"

echo "[INFO] Compressing"
xz -T0 -6 -f "$image"
(cd "$WORK" && sha256sum "$(basename "$image").xz" > "$(basename "$image").xz.sha256")
mv -f "$image.xz" "$image.xz.sha256" artifacts/
ls -lh "artifacts/$(basename "$image").xz"
echo "[INFO] Built artifacts/$(basename "$image").xz: write it with Raspberry Pi Imager (Use custom), with a user and Wi-Fi in its settings"
