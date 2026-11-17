#!/usr/bin/env bash
# Writes the PI.N.S. Raspberry Pi image (make pi-image) to an SD card, with what Raspberry Pi Imager's settings would add: a hostname, a user
# (sudo with its password, the SSH keys in ~/.ssh/id_*.pub and PI_SSH_KEYS), the Wi-Fi, and SSH on. Raspberry Pi OS's cloud-init reads them
# from the card's boot partition at the first boot. The site (latitude and longitude, decimal degrees, east positive) goes there too, in
# pins.conf, which the image's first boot puts in PI.N.S.'s profile.
#
# make pi-image also puts this script in front of the image in one executable file, pins-origin_<version>_rpi-arm64.run, which writes the
# image it carries: its build fills in PAYLOAD_SHA256 and PAYLOAD_NAME below, and appends the image after the line __PINS_IMAGE__.
#
# Usage: packaging/flash-pi-card.sh <device, as /dev/sdb> [hostname] [image]
#        pins-origin_<version>_rpi-arm64.run <device, as /dev/sdb> [hostname]
#   hostname  default pins-origin; image  default the newest artifacts/*_rpi-arm64.img.xz
#   asks for the Wi-Fi network and its password, the user's password and the site, offering WIFI_SSID and SITE (latitude,longitude) if set
#   CONFIG_ONLY=<dir> writes user-data, network-config and pins.conf there and stops, without touching the device
set -Eeuo pipefail

PAYLOAD_SHA256=
PAYLOAD_NAME=
self="$(readlink -f "${BASH_SOURCE[0]}")"

fail() {
  echo "[ERROR] $*" >&2
  exit 1
}

DEVICE="${1:?Usage: $0 <device, as /dev/sdb> [hostname]$([[ -n "$PAYLOAD_SHA256" ]] || echo ' [image]')}"
HOST="${2:-pins-origin}"
USER_NAME="${USER_NAME:-$USER}"
# The image: the one this file carries, or one built in this checkout
if [[ -n "$PAYLOAD_SHA256" ]]; then
  IMAGE="$PAYLOAD_NAME"
  payload_line="$(awk '/^__PINS_IMAGE__$/ { print NR + 1; exit }' "$self")"
  image() { tail -n +"$payload_line" "$self"; }
else
  cd "$(dirname "$self")/.."
  IMAGE="${3:-$(ls -t artifacts/*_rpi-arm64.img.xz 2>/dev/null | head -1)}"
  [[ -f "$IMAGE" ]] || fail "No image: run make pi-image first"
  image() { cat "$IMAGE"; }
fi
if [[ -z "${CONFIG_ONLY:-}" ]]; then
  [[ -b "$DEVICE" ]] || fail "$DEVICE is not a block device"
  read -r removable size < <(lsblk -bdno RM,SIZE "$DEVICE")
  # only a removable card no bigger than 256 GB: never a system disk
  [[ "$removable" == 1 ]] || fail "$DEVICE is not a removable disk"
  (( size <= 256 * 1000 ** 3 )) || fail "$DEVICE is bigger than an SD card should be"
fi

keys=()
for key in ~/.ssh/id_*.pub ${PI_SSH_KEYS:-}; do
  [[ -f "$key" ]] && keys+=("$(cat "$key")")
done

read -r -p "Wi-Fi network the Pi joins${WIFI_SSID:+ [$WIFI_SSID]}: " ssid
WIFI_SSID="${ssid:-${WIFI_SSID:-}}"
[[ -n "$WIFI_SSID" ]] || fail "The Pi needs a Wi-Fi network to be reached on"
read -r -s -p "Wi-Fi password for $WIFI_SSID: " wifi_password; echo
read -r -s -p "Password for $USER_NAME on $HOST (for sudo): " user_password; echo
[[ -n "$user_password" ]] || fail "The user needs a password"
password_hash="$(openssl passwd -6 -stdin <<< "$user_password")"
# degree <prompt> <default> <limit>: decimal degrees, within +-limit
degree() {
  local value
  read -r -p "$1 in decimal degrees${2:+ [$2]}: " value
  value="${value:-$2}"
  [[ "$value" =~ ^-?[0-9]+(\.[0-9]+)?$ ]] && python3 -I -c 'import sys; sys.exit(abs(float(sys.argv[1])) > float(sys.argv[2]))' "$value" "$3" ||
    fail "$1 $value is not decimal degrees within ±$3"
  echo "$value"
}
site_latitude=; site_longitude=
[[ "${SITE:-}" != *,* ]] || { site_latitude="${SITE%%,*}"; site_longitude="${SITE#*,}"; }
latitude="$(degree "Site latitude (north positive)" "$site_latitude" 90)"
longitude="$(degree "Site longitude (east positive)" "$site_longitude" 180)"

config="$(mktemp -d)"
trap 'rm -rf "$config"' EXIT
# JSON strings are YAML strings: quotes and backslashes in passwords and names come through
json() {
  python3 -I -c 'import json, sys; print(json.dumps(sys.argv[1]))' "$1"
}
{
  echo "#cloud-config"
  echo "hostname: $(json "$HOST")"
  echo "manage_etc_hosts: true"
  echo "timezone: Europe/London"
  echo "keyboard: {model: pc105, layout: gb}"
  echo "users:"
  echo "- name: $(json "$USER_NAME")"
  echo "  groups: users,adm,dialout,audio,netdev,video,plugdev,cdrom,games,input,gpio,spi,i2c,render,sudo"
  echo "  shell: /bin/bash"
  echo "  lock_passwd: false"
  echo "  passwd: $(json "$password_hash")"
  if (( ${#keys[@]} )); then
    echo "  ssh_authorized_keys:"
    for key in "${keys[@]}"; do echo "  - $(json "$key")"; done
  fi
  echo "enable_ssh: true"
  echo "ssh_pwauth: $( (( ${#keys[@]} )) && echo false || echo true)"
  echo "runcmd:"
  echo "- [systemctl, enable, --now, ssh]"
} > "$config/user-data"
# Only the Wi-Fi: the Ethernet port is the image's own shared connection for the Origin
cat > "$config/network-config" <<EOF
network:
  version: 2
  wifis:
    wlan0:
      dhcp4: true
      optional: true
      regulatory-domain: GB
      access-points:
        $(json "$WIFI_SSID"):
          password: $(json "$wifi_password")
EOF
# Read by the image's first boot (packaging/pi-image/pins-firstboot.sh) as shell: the values are checked numbers
printf 'latitude=%s\nlongitude=%s\n' "$latitude" "$longitude" > "$config/pins.conf"

if [[ -n "${CONFIG_ONLY:-}" ]]; then
  mkdir -p "$CONFIG_ONLY"
  cp "$config/user-data" "$config/network-config" "$config/pins.conf" "$CONFIG_ONLY/"
  echo "[INFO] Wrote $CONFIG_ONLY/user-data, network-config and pins.conf (${#keys[@]} SSH keys)"
  exit 0
fi

lsblk -o NAME,SIZE,RM,TRAN,MODEL,LABEL "$DEVICE"
read -r -p "Erase $DEVICE and write $(basename "$IMAGE") as $HOST? Type the device name to go on: " answer
[[ "$answer" == "$DEVICE" ]] || fail "Stopped: nothing written"

if [[ -n "$PAYLOAD_SHA256" ]]; then
  echo "[INFO] Checking the image this file carries"
  [[ "$(image | sha256sum | cut -d' ' -f1)" == "$PAYLOAD_SHA256" ]] || fail "The image in $(basename "$self") is damaged: download it again"
elif [[ -f "$IMAGE.sha256" ]]; then
  (cd "$(dirname "$IMAGE")" && sha256sum -c --quiet "$(basename "$IMAGE").sha256") || fail "$IMAGE does not match its checksum"
fi
for part in $(lsblk -lno PATH,MOUNTPOINTS "$DEVICE" | awk 'NF == 2 { print $1 }'); do
  udisksctl unmount -b "$part" > /dev/null
done

echo "[INFO] Writing $(basename "$IMAGE") to $DEVICE"
image | xz -dc | sudo dd of="$DEVICE" bs=4M conv=fsync status=progress
sudo partprobe "$DEVICE" 2>/dev/null || sudo blockdev --rereadpt "$DEVICE"
udevadm settle

boot="$(lsblk -lno PATH "$DEVICE" | sed -n 2p)"
mountpoint="$(udisksctl mount -b "$boot" | sed -n 's/^Mounted .* at \(.*\)$/\1/p')"
[[ -d "$mountpoint" ]] || fail "Could not mount $boot to add the settings"
cp "$config/user-data" "$config/network-config" "$config/pins.conf" "$mountpoint/"
sync
udisksctl unmount -b "$boot" > /dev/null
udisksctl power-off -b "$DEVICE" 2>/dev/null || true
echo "[INFO] Done: $HOST with user $USER_NAME (${#keys[@]} SSH keys) on $WIFI_SSID, site $latitude, $longitude. Take the card out and boot the Pi with it."
echo "[INFO] The first boot installs PI.N.S. from the card, a few minutes (journalctl -u pins-firstboot), then it is at http://$HOST.local:5000"
# The end of the script: in the .run file the image follows
exit 0
