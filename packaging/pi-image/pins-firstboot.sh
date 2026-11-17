#!/bin/sh
# First boot of the PI.N.S. Raspberry Pi image: installs the pins-origin package the image carries, with the Debian packages it needs
# (OpenCV and the rest, and astrometry.net for plate solving), which the image also carries in /opt/pins-image/debs, so no internet is
# needed; then starts PI.N.S. as the user made in Raspberry Pi Imager. Run by pins-firstboot.service until it succeeds; its log is
# journalctl -u pins-firstboot.
set -eu

deb="$(ls /opt/pins-image/pins-origin_*.deb)"

# Raspberry Pi Imager's settings make the user during this first boot
for i in $(seq 120); do
    user="$(getent passwd 1000 | cut -d: -f1)"
    [ -n "$user" ] && [ "$user" != rpi-first-boot-wizard ] && break
    sleep 5
done
[ -n "$user" ] || { echo "No user with UID 1000: set one in Raspberry Pi Imager"; exit 1; }

# The packages new to the system are marked as installed for pins-origin, not wanted for themselves (astrometry.net excepted), as apt
# would have marked them; one already installed and upgraded here keeps its mark
new="$(for package in /opt/pins-image/debs/*.deb; do
    name="$(dpkg-deb -f "$package" Package)"
    [ "$(dpkg-query -W -f '${db:Status-Status}' "$name" 2>/dev/null)" = installed ] || [ "$name" = astrometry.net ] || echo "$name"
done)"
# From the files alone: no apt-get update, so neither the internet nor the clock (the Pi has no clock battery) matters
DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "$deb" /opt/pins-image/debs/*.deb
[ -z "$new" ] || echo "$new" | xargs apt-mark auto > /dev/null
rm -rf /opt/pins-image/debs     # about 850 MB: room on small cards
systemctl enable --now "pins@$user.service"
touch /var/lib/pins-image-installed
echo "PI.N.S. installed and started as $user: http://$(hostname).local:5000"
