#!/usr/bin/env bash
# Writes the PI.N.S. Raspberry Pi image (make pi-image) to an SD card, with what Raspberry Pi Imager's settings would add: a hostname, a user
# (sudo with its password, the SSH keys in ~/.ssh/id_*.pub and PI_SSH_KEYS), the Wi-Fi, and SSH on. Raspberry Pi OS's cloud-init reads them
# from the card's boot partition at the first boot.
#
# Usage: packaging/flash-pi-card.sh <device, as /dev/sdb> [hostname] [image]
#   hostname  default origin6; image  default the newest artifacts/*_rpi-arm64.img.xz
#   WIFI_SSID default "YourNetwork"; asks for the Wi-Fi password and the user's password
#   CONFIG_ONLY=<dir> writes user-data and network-config there and stops, without touching the device
set -Eeuo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

fail() {
  echo "[ERROR] $*" >&2
  exit 1
}

DEVICE="${1:?Usage: $0 <device, as /dev/sdb> [hostname] [image]}"
HOST="${2:-origin6}"
IMAGE="${3:-$(ls -t artifacts/*_rpi-arm64.img.xz 2>/dev/null | head -1)}"
WIFI_SSID="${WIFI_SSID:-YourNetwork}"
USER_NAME="${USER_NAME:-$USER}"

[[ -f "$IMAGE" ]] || fail "No image: run make pi-image first"
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

read -r -s -p "Wi-Fi password for $WIFI_SSID: " wifi_password; echo
read -r -s -p "Password for $USER_NAME on $HOST (for sudo): " user_password; echo
[[ -n "$user_password" ]] || fail "The user needs a password"
password_hash="$(openssl passwd -6 -stdin <<< "$user_password")"

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

if [[ -n "${CONFIG_ONLY:-}" ]]; then
  mkdir -p "$CONFIG_ONLY"
  cp "$config/user-data" "$config/network-config" "$CONFIG_ONLY/"
  echo "[INFO] Wrote $CONFIG_ONLY/user-data and network-config (${#keys[@]} SSH keys)"
  exit 0
fi

lsblk -o NAME,SIZE,RM,TRAN,MODEL,LABEL "$DEVICE"
read -r -p "Erase $DEVICE and write $(basename "$IMAGE") as $HOST? Type the device name to go on: " answer
[[ "$answer" == "$DEVICE" ]] || fail "Stopped: nothing written"

if [[ -f "$IMAGE.sha256" ]]; then
  (cd "$(dirname "$IMAGE")" && sha256sum -c --quiet "$(basename "$IMAGE").sha256") || fail "$IMAGE does not match its checksum"
fi
for part in $(lsblk -lno PATH,MOUNTPOINTS "$DEVICE" | awk 'NF == 2 { print $1 }'); do
  udisksctl unmount -b "$part" > /dev/null
done

echo "[INFO] Writing $(basename "$IMAGE") to $DEVICE"
xz -dc "$IMAGE" | sudo dd of="$DEVICE" bs=4M conv=fsync status=progress
sudo partprobe "$DEVICE" 2>/dev/null || sudo blockdev --rereadpt "$DEVICE"
udevadm settle

boot="$(lsblk -lno PATH "$DEVICE" | sed -n 2p)"
mountpoint="$(udisksctl mount -b "$boot" | sed -n 's/^Mounted .* at \(.*\)$/\1/p')"
[[ -d "$mountpoint" ]] || fail "Could not mount $boot to add the settings"
cp "$config/user-data" "$config/network-config" "$mountpoint/"
sync
udisksctl unmount -b "$boot" > /dev/null
udisksctl power-off -b "$DEVICE" 2>/dev/null || true
echo "[INFO] Done: $HOST with user $USER_NAME (${#keys[@]} SSH keys) on $WIFI_SSID. Take the card out and boot the Pi with it."
echo "[INFO] The first boot installs PI.N.S. from the card, a few minutes (journalctl -u pins-firstboot), then it is at http://$HOST.local:5000"
