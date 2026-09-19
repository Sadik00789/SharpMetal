#!/usr/bin/env bash
# Capture xHCI bisect tokens from a serial console attached to real hardware.
#
# Usage: Capture-Xhci-Serial.sh [serial-device] [baud] [output-file]
#   serial-device  default /dev/ttyUSB0
#   baud           default 115200
#   output-file    default xhci-serial.log
#
# Reads the serial port, tees everything to the output file, and prints only the
# lines that matter for the USB keyboard bisect.
set -euo pipefail

DEV="${1:-/dev/ttyUSB0}"
BAUD="${2:-115200}"
OUT="${3:-xhci-serial.log}"

if [[ ! -e "${DEV}" ]]; then
    echo "[-] Serial device ${DEV} not found. Pass the correct one, e.g. /dev/ttyUSB0 or /dev/ttyS0." >&2
    exit 1
fi

if ! command -v stty >/dev/null 2>&1; then
    echo "[-] stty is required to configure the serial port." >&2
    exit 1
fi

# raw 8N1, no flow control. May need sudo depending on the device.
stty -F "${DEV}" "${BAUD}" cs8 -cstopb -parenb -crtscts raw -echo

echo "[CAPTURE] ${DEV} @ ${BAUD} -> ${OUT} (Ctrl-C to stop)"
echo "[CAPTURE] Watching for /\\[XHCI\\]|\\[PASS\\] XHCI|\\[INPUT\\]/"
echo "-----------------------------------------------------------------"

tee "${OUT}" < "${DEV}" | grep --line-buffered -E '\[XHCI\]|\[PASS\] XHCI|\[INPUT\]'
