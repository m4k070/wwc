#!/usr/bin/env bash
# Wrapper to run Playwright test with proper LD_LIBRARY_PATH

# Find library paths dynamically
GLIB_PATH=$(dirname $(ls /nix/store/*glib*/lib/libglib-2.0.so.0 2>/dev/null | head -1))
NSS_PATH=$(dirname $(ls /nix/store/*nss*/lib/libnss3.so 2>/dev/null | head -1))
NSPR_PATH=$(dirname $(ls /nix/store/*nspr*/lib/libnspr4.so 2>/dev/null | head -1))
DBUS_PATH=$(dirname $(ls /nix/store/*dbus*/lib/libdbus-1.so 2>/dev/null | head -1))
ATSPI_PATH=$(dirname $(ls /nix/store/*at-spi2*/lib/libatspi.so 2>/dev/null | head -1))
CUPS_PATH=$(dirname $(ls /nix/store/*cups*/lib/libcups.so 2>/dev/null | head -1))
XKBFILE_PATH=$(dirname $(ls /nix/store/*libxkbfile*/lib/libxkbfile.so 2>/dev/null | head -1))
XCOMPOSITE_PATH=$(dirname $(ls /nix/store/*libxcomposite*/lib/libXcomposite.so 2>/dev/null | head -1))
XDAMAGE_PATH=$(dirname $(ls /nix/store/*libxdamage*/lib/libXdamage.so 2>/dev/null | head -1))
XFIXES_PATH=$(dirname $(ls /nix/store/*libxfixes*/lib/libXfixes.so 2>/dev/null | head -1))
XRANDR_PATH=$(dirname $(ls /nix/store/*libxrandr*/lib/libXrandr.so 2>/dev/null | head -1))
GBM_PATH=$(dirname $(ls /nix/store/*libgbm*/lib/libgbm.so 2>/dev/null | head -1))
ASOUND_PATH=$(dirname $(ls /nix/store/*alsa-lib*/lib/libasound.so 2>/dev/null | head -1))
PULSE_PATH=$(dirname $(ls /nix/store/*pulseaudio*/lib/libpulse.so 2>/dev/null | head -1))

export LD_LIBRARY_PATH="${GLIB_PATH}:${NSS_PATH}:${NSPR_PATH}:${DBUS_PATH}:${ATSPI_PATH}:${CUPS_PATH}:${XKBFILE_PATH}:${XCOMPOSITE_PATH}:${XDAMAGE_PATH}:${XFIXES_PATH}:${XRANDR_PATH}:${GBM_PATH}:${ASOUND_PATH}:${PULSE_PATH}:${LD_LIBRARY_PATH}"

echo "LD_LIBRARY_PATH=$LD_LIBRARY_PATH"

# Run the test
cd /home/makoto/sandbox/wwc
exec npx playwright test -c web/playwright.config.ts "$@"