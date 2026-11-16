#!/bin/sh
# First boot of the PI.N.S. Raspberry Pi image: installs the pins-origin package the image carries, with the Debian packages it needs
# (OpenCV and the rest, from the internet: about 850 MB), and astrometry.net for plate solving, then starts PI.N.S. as the user made in
# Raspberry Pi Imager. Run by pins-firstboot.service until it succeeds; its log is journalctl -u pins-firstboot.
set -eu

deb="$(ls /opt/pins-image/pins-origin_*.deb)"

# Raspberry Pi Imager's settings make the user and join the Wi-Fi during this first boot
for i in $(seq 120); do
    user="$(getent passwd 1000 | cut -d: -f1)"
    [ -n "$user" ] && [ "$user" != rpi-first-boot-wizard ] && getent hosts deb.debian.org > /dev/null && break
    sleep 5
done
[ -n "$user" ] || { echo "No user with UID 1000: set one in Raspberry Pi Imager"; exit 1; }
getent hosts deb.debian.org > /dev/null || { echo "No internet: the first boot downloads the libraries PI.N.S. needs"; exit 1; }

apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "$deb" astrometry.net
systemctl enable --now "pins@$user.service"
touch /var/lib/pins-image-installed
echo "PI.N.S. installed and started as $user: http://$(hostname).local:5000"
