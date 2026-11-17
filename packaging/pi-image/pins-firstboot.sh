#!/bin/sh
# First boot of the PI.N.S. Raspberry Pi image: installs the pins-origin package the image carries, with the Debian packages it needs
# (OpenCV and the rest, and astrometry.net for plate solving), which the image also carries in /opt/pins-image/debs, so no internet is
# needed; then starts PI.N.S. as the user made in Raspberry Pi Imager and sets its profile up for the Origin, at the site that
# packaging/flash-pi-card.sh wrote into pins.conf on the boot partition. Run by pins-firstboot.service until it succeeds; its log is
# journalctl -u pins-firstboot.
set -eu

deb="$(ls /opt/pins-image/pins-origin_*.deb)"
conf=/boot/firmware/pins.conf

# Raspberry Pi Imager's settings make the user during this first boot
for i in $(seq 120); do
    user="$(getent passwd 1000 | cut -d: -f1)"
    [ -n "$user" ] && [ "$user" != rpi-first-boot-wizard ] && break
    sleep 5
done
[ -n "$user" ] || { echo "No user with UID 1000: set one in Raspberry Pi Imager"; exit 1; }

if [ ! -e /var/lib/pins-image-packages ]; then
    # The packages new to the system are marked as installed for pins-origin, not wanted for themselves (astrometry.net excepted), as apt
    # would have marked them; one already installed and upgraded here keeps its mark
    new="$(for package in /opt/pins-image/debs/*.deb; do
        name="$(dpkg-deb -f "$package" Package)"
        [ "$(dpkg-query -W -f '${db:Status-Status}' "$name" 2>/dev/null)" = installed ] || [ "$name" = astrometry.net ] || echo "$name"
    done)"
    # From the files alone, as the image's build checked: with no sources, apt cannot take the same versions from the archives instead, as
    # it did with the internet up (2026-11-17), so neither the internet nor the clock (the Pi has no clock battery) matters
    no_sources="$(mktemp -d)"
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        -o Dir::Etc::sourcelist=/dev/null -o Dir::Etc::sourceparts="$no_sources" "$deb" /opt/pins-image/debs/*.deb
    rmdir "$no_sources"
    [ -z "$new" ] || echo "$new" | xargs apt-mark auto > /dev/null
    rm -rf /opt/pins-image/debs     # about 160 MB: room on small cards
    touch /var/lib/pins-image-packages
fi
# LiveStack, which PI.N.S. loads from the user's plugins as it would a plugin installed from its list
plugins="$(getent passwd "$user" | cut -d: -f6)/.local/share/NINA/Plugins/3.0.0"
if [ ! -e "$plugins/Livestack/nina.plugin.livestack.dll" ]; then
    runuser -u "$user" -- mkdir -p "$plugins/Livestack"
    runuser -u "$user" -- cp /opt/pins-image/Livestack/nina.plugin.livestack.dll /opt/pins-image/Livestack/MathNet.Numerics.dll \
        /opt/pins-image/Livestack/LICENSE.txt "$plugins/Livestack/"
fi
systemctl enable --now "pins@$user.service"

# The profile, through the Advanced API as the web page sets it, once PI.N.S. answers: the Origin's devices, plate solving binned 2x2 as
# suits the Origin's frames, and the site from pins.conf
api=http://localhost:1888/v2/api
for i in $(seq 60); do
    curl -fs -m 5 "$api/version" > /dev/null && break
    sleep 5
done
latitude=; longitude=
[ ! -f "$conf" ] || . "$conf"
set_profile() {
    curl -fsS -m 30 -G "$api/profile/change-value" --data-urlencode "settingpath=$1" --data-urlencode "newValue=$2" | grep -q '"Success":true' ||
        { echo "Could not set $1 to $2 in the profile"; exit 1; }
}
set_profile CameraSettings-Id Origin_Camera
set_profile TelescopeSettings-Id Origin_Mount
set_profile FocuserSettings-Id Origin_Focuser
set_profile PlateSolveSettings-DownSampleFactor 2
if [ -n "$latitude" ] && [ -n "$longitude" ]; then
    set_profile AstrometrySettings-Latitude "$latitude"
    set_profile AstrometrySettings-Longitude "$longitude"
else
    echo "No site in $conf: set it in the web page"
fi
touch /var/lib/pins-image-installed
echo "PI.N.S. installed and started as $user, set up for the Origin at ${latitude:-?}, ${longitude:-?}: http://$(hostname).local:5000"
