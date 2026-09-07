#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

export PATH="${HOME}/.dotnet:${HOME}/.local/bin:${PATH}"
export LD_LIBRARY_PATH="${HOME}/.local/usr/lib64:${LD_LIBRARY_PATH:-}"

# Locate OVMF firmware paths (Multi-distro probe)
OVMF_CODE=""
OVMF_VARS=""

candidates=(
    "/usr/share/OVMF/OVMF_CODE.fd:/usr/share/OVMF/OVMF_VARS.fd"
    "/usr/share/ovmf/OVMF.fd:"
    "/usr/share/edk2/ovmf/OVMF_CODE.fd:/usr/share/edk2/ovmf/OVMF_VARS.fd"
    "${HOME}/.local/usr/share/edk2/ovmf/OVMF_CODE.fd:${HOME}/.local/usr/share/edk2/ovmf/OVMF_VARS.fd"
    "/usr/share/edk2-ovmf/OVMF_CODE.fd:/usr/share/edk2-ovmf/OVMF_VARS.fd"
)

for pair in "${candidates[@]}"; do
    code="${pair%%:*}"
    vars="${pair##*:}"
    if [[ -f "${code}" ]]; then
        OVMF_CODE="${code}"
        OVMF_VARS="${vars}"
        break
    fi
done

if [[ -z "${OVMF_CODE}" ]]; then
    echo "[ERROR] Could not find valid OVMF firmware image." >&2
    exit 1
fi

echo "[QEMU] Using OVMF firmware: CODE=${OVMF_CODE}, VARS=${OVMF_VARS:-none}"

OVMF_FLASH_ARGS=()
if [[ -n "${OVMF_VARS}" && -f "${OVMF_VARS}" ]]; then
    VARS_TMP=$(mktemp /tmp/ovmf_vars.XXXXXX.fd)
    cp "${OVMF_VARS}" "${VARS_TMP}"
    trap 'rm -f "${VARS_TMP}"' EXIT
    OVMF_FLASH_ARGS+=("-drive" "if=pflash,format=raw,readonly=on,file=${OVMF_CODE}")
    OVMF_FLASH_ARGS+=("-drive" "if=pflash,format=raw,file=${VARS_TMP}")
else
    OVMF_FLASH_ARGS+=("-bios" "${OVMF_CODE}")
fi

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

NET_ARGS=()
if [[ "$*" != *"-device virtio-net"* ]]; then
    NET_ARGS+=("-netdev" "user,id=net0" "-device" "virtio-net-pci,netdev=net0")
fi

HEADLESS_FLAGS=()
if [[ "$*" == *"--headless"* ]] || [[ "${CI:-}" == "true" ]]; then
    HEADLESS_FLAGS=("-display" "none" "-vga" "none" "-serial" "stdio" "-no-reboot")
fi

EXTRA_ARGS=()
for arg in "$@"; do
    if [[ "$arg" != "--headless" ]]; then
        EXTRA_ARGS+=("$arg")
    fi
done

# Run QEMU with serial output, debugcon, GDB stub option, and isa-debug-exit
"${QEMU_BIN}" \
    -machine q35 \
    -cpu max \
    "${OVMF_FLASH_ARGS[@]}" \
    "${QEMU_DRIVE_ARGS[@]}" \
    "${NVME_ARGS[@]}" \
    "${NET_ARGS[@]}" \
    -smp 4 \
    -m 4G \
    ${HEADLESS_FLAGS[@]:--display none -serial stdio -no-reboot} \
    -d int,cpu_reset \
    -D "${REPO_ROOT}/qemu.log" \
    -device isa-debug-exit,iobase=0xf4,iosize=0x04 \
    "${EXTRA_ARGS[@]}"
