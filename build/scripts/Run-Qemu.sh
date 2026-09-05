#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

export PATH="${HOME}/.dotnet:${HOME}/.local/bin:${PATH}"
export LD_LIBRARY_PATH="${HOME}/.local/usr/lib64:${LD_LIBRARY_PATH:-}"

# Locate OVMF firmware paths
OVMF_DIR=""
for candidate in \
    "/usr/share/edk2/ovmf" \
    "${HOME}/.local/usr/share/edk2/ovmf" \
    "/usr/share/OVMF" \
    "/usr/share/edk2-ovmf"; do
    if [[ -d "${candidate}" && -f "${candidate}/OVMF_CODE.fd" ]]; then
        OVMF_DIR="${candidate}"
        break
    fi
done

if [[ -z "${OVMF_DIR}" ]]; then
    echo "[ERROR] OVMF firmware not found in /usr/share/edk2/ovmf or fallback locations." >&2
    exit 1
fi

echo "[QEMU] Using OVMF firmware from ${OVMF_DIR}"

OVMF_CODE="${OVMF_DIR}/OVMF_CODE.fd"
OVMF_VARS="${OVMF_DIR}/OVMF_VARS.fd"

# Copy VARS to a temporary file so firmware nvram writes don't corrupt the master
VARS_TMP=$(mktemp /tmp/ovmf_vars.XXXXXX.fd)
cp "${OVMF_VARS}" "${VARS_TMP}"
trap 'rm -f "${VARS_TMP}"' EXIT

QEMU_BIN="qemu-system-x86_64"
if ! command -v "${QEMU_BIN}" >/dev/null 2>&1; then
    if [[ -x "${HOME}/.local/bin/qemu-system-x86_64" ]]; then
        QEMU_BIN="${HOME}/.local/bin/qemu-system-x86_64"
    else
        echo "[ERROR] qemu-system-x86_64 not found." >&2
        exit 1
    fi
fi

DISK_IMG="${REPO_ROOT}/build/disk.img"
ESP_DIR="${REPO_ROOT}/build/esp"

QEMU_DRIVE_ARGS=()
if [[ -f "${DISK_IMG}" ]]; then
    QEMU_DRIVE_ARGS+=("-drive" "format=raw,file=${DISK_IMG}")
elif [[ -d "${ESP_DIR}" ]]; then
    QEMU_DRIVE_ARGS+=("-drive" "format=raw,file=fat:rw:${ESP_DIR}")
else
    echo "[ERROR] Neither ${DISK_IMG} nor ${ESP_DIR} found. Run Make-DiskImage.sh first." >&2
    exit 1
fi

NVME_IMG="${REPO_ROOT}/build/nvme.img"
if [[ ! -f "${NVME_IMG}" ]]; then
    dd if=/dev/zero of="${NVME_IMG}" bs=1M count=32 status=none
fi

NVME_ARGS=()
if [[ "$*" != *"-device nvme"* ]]; then
    NVME_ARGS+=("-drive" "file=${NVME_IMG},format=raw,if=none,id=nvm" "-device" "nvme,serial=nvme01,drive=nvm")
fi

# Run QEMU with serial output, debugcon, GDB stub option, and isa-debug-exit
"${QEMU_BIN}" \
    -machine q35 \
    -cpu max \
    -drive "if=pflash,format=raw,readonly=on,file=${OVMF_CODE}" \
    -drive "if=pflash,format=raw,file=${VARS_TMP}" \
    "${QEMU_DRIVE_ARGS[@]}" \
    "${NVME_ARGS[@]}" \
    -m 512M \
    -no-reboot \
    -display none \
    -serial stdio \
    -d int,cpu_reset \
    -D "${REPO_ROOT}/qemu.log" \
    -device isa-debug-exit,iobase=0xf4,iosize=0x04 \
    "$@"
