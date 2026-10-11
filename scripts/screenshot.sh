#!/usr/bin/env bash
#
# Runs the real application on a virtual display and saves what it looks like.
#
# The headless rendering tests cover the views. This covers what they cannot: Program.Main, the
# single-instance mutex, creating the database, applying the migrations, seeding the admin, and
# the window manager actually placing a window. That path only ever runs on a developer's desk,
# which is why a startup mistake can sit in the tree for weeks unnoticed.
#
# Usage:
#   scripts/screenshot.sh [name]
#
# Writes screenshots/<name>.png. Set XDG_DATA_HOME to keep the database somewhere disposable; by
# default the app uses the real profile and would find the shop's own data.

set -euo pipefail

name="${1:-app}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/screenshots/$name.png"
display="${DAFTAR_DISPLAY:-:99}"
size="${DAFTAR_SIZE:-1400x950x24}"
wait="${DAFTAR_WAIT:-20}"

for tool in Xvfb import; do
    command -v "$tool" >/dev/null || {
        echo "missing: $tool" >&2
        echo "  Debian/Ubuntu: sudo apt install xvfb imagemagick" >&2
        exit 1
    }
done

mkdir -p "$root/screenshots"

started_display=0
if ! pgrep -f "Xvfb $display" >/dev/null 2>&1; then
    Xvfb "$display" -screen 0 "$size" >/tmp/daftar-xvfb.log 2>&1 &
    started_display=1
    sleep 2
fi

cleanup() {
    pkill -f "Daftar.Desktop" 2>/dev/null || true
    if [ "$started_display" = 1 ]; then
        pkill -f "Xvfb $display" 2>/dev/null || true
    fi
}
trap cleanup EXIT

echo "starting Daftar on $display, waiting ${wait}s for the window..."
# No --no-build here. A screenshot of a stale binary is worse than no screenshot: it shows a bug
# that was already fixed, or hides one that is not.
DISPLAY="$display" timeout "$((wait + 60))" \
    dotnet run --project "$root/src/Daftar.Desktop" >/tmp/daftar-app.log 2>&1 &

sleep "$wait"

if ! DISPLAY="$display" import -window root "$out" 2>/dev/null; then
    echo "capture failed; the app log follows" >&2
    tail -30 /tmp/daftar-app.log >&2
    exit 1
fi

echo "wrote $out"
tail -5 /tmp/daftar-app.log || true
