#!/usr/bin/env bash
#
# Runs the real application on a virtual display, signs in, and saves what it looks like.
#
# The headless rendering tests cover the views. This covers what they cannot: Program.Main, the
# single-instance mutex, creating the database, applying the migrations, seeding the admin, and a
# window manager actually placing a window. That path only ever runs on a developer's desk, which
# is why a startup mistake can sit in the tree for weeks unnoticed. It is also how the "False"
# next to the password box was found.
#
# Usage:
#   scripts/screenshot.sh
#
# Writes screenshots/live-*.png. Needs: xvfb, imagemagick, xdotool, sqlite3.
#
# Only the screens that open reliably are captured. Driving the rest by pixel is not worth it:
# opening a page replaces the start menu, and the ribbon offsets shift with the window size, so a
# sweep written against one 1500x980 capture silently clicks the wrong control on any other.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/screenshots"
data="${DAFTAR_DATA_HOME:-/tmp/daftar-shots}"
db="$data/Daftar/daftar.db"
display="${DAFTAR_DISPLAY:-:99}"
size="${DAFTAR_SIZE:-1500x980x24}"
wait="${DAFTAR_WAIT:-20}"

for tool in Xvfb import xdotool sqlite3; do
    command -v "$tool" >/dev/null || {
        echo "missing: $tool" >&2
        echo "  sudo apt install xvfb imagemagick xdotool sqlite3" >&2
        exit 1
    }
done

mkdir -p "$out"

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

export DISPLAY="$display"

shot()  { import -window root "$out/$1.png"; echo "  $1"; }
typ()   { xdotool type --clearmodifiers "$1"; sleep 0.5; }
tap()   { xdotool key --clearmodifiers "$1"; sleep "${2:-1.2}"; }
click() { xdotool mousemove "$1" "$2" click 1; sleep "${3:-3}"; }

run_app() {
    XDG_DATA_HOME="$data" dotnet run --project "$root/src/Daftar.Desktop" >>/tmp/daftar-app.log 2>&1 &
    sleep "$wait"
}

# No --no-build anywhere: a screenshot of a stale binary is worse than no screenshot. It shows a
# bug that was already fixed, or hides one that is not.

echo "1. first run: creates and migrates the database"
rm -rf "$data"
: >/tmp/daftar-app.log
run_app
pkill -f "Daftar.Desktop" 2>/dev/null || true
sleep 2

# The seeded admin is made to change its password on first sign-in. Clicking through that form is
# brittle -- the panel shifts by ~31px depending on whether a banner is showing -- and it is
# already covered by a rendering test, so the flag is cleared in the database instead.
sqlite3 "$db" "UPDATE Users SET MustChangePassword = 0 WHERE Username = 'admin';" >/dev/null

echo "2. sign in"
run_app
click 530 507
typ admin
tap Return 8
shot live-01-dashboard

echo "3. open the sell page"
click 806 227 4
shot live-02-sale

echo
echo "wrote $out/live-*.png"
