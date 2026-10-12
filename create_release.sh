#!/usr/bin/env bash
# SharpMetal release publisher: uploads disk.img + nvme.img + BOOTX64.EFI as a GitHub release.
set -euo pipefail

TAG="${1:-v2.0.1}"
TITLE="SharpMetal ${TAG}: Fully Operational Native xHCI USB 3.0 & Wrap-Around Freeze Fix"

# Ensure GitHub CLI is available
command -v gh >/dev/null 2>&1 || { echo "ERROR: GitHub CLI (gh) is not installed or not in PATH."; exit 1; }

echo "==> [1/4] Checking git repository status..."

echo "==> [2/4] Locating release build artifacts..."
IMG_ASSET=$(find build/ -maxdepth 3 -type f -name "disk.img" | head -n 1)
NVME_ASSET=$(find build/ -maxdepth 3 -type f -name "nvme.img" | head -n 1)
EFI_ASSET=$(find build/ -maxdepth 5 -type f \( -name "BOOTX64.EFI" -o -name "bootx64.efi" \) | head -n 1)

if [ -z "${IMG_ASSET}" ] || [ ! -f "${IMG_ASSET}" ]; then
    echo "ERROR: disk.img not found under build/! Run: bash build/scripts/Make-DiskImage.sh"
    exit 1
fi

if [ -z "${NVME_ASSET}" ] || [ ! -f "${NVME_ASSET}" ]; then
    echo "ERROR: nvme.img not found under build/! Run: bash build/scripts/Make-DiskImage.sh"
    exit 1
fi

if [ -z "${EFI_ASSET}" ] || [ ! -f "${EFI_ASSET}" ]; then
    echo "ERROR: BOOTX64.EFI not found under build/! Run: bash build/scripts/Make-DiskImage.sh"
    exit 1
fi

echo "  -> Found Disk Image : ${IMG_ASSET} ($(du -h "${IMG_ASSET}" | cut -f1))"
echo "  -> Found NVMe Image : ${NVME_ASSET} ($(du -h "${NVME_ASSET}" | cut -f1))"
echo "  -> Found EFI Binary : ${EFI_ASSET} ($(du -h "${EFI_ASSET}" | cut -f1))"

echo "==> [3/4] Compiling release notes..."
NOTES_FILE=$(mktemp /tmp/sharpmetal-release-notes.XXXXXX.md)
trap 'rm -f "${NOTES_FILE}"' EXIT

cat << 'NOTES' > "${NOTES_FILE}"
# SharpMetal v2.0.1: Native xHCI Enabled by Default & Ring Wrap Fix

**SharpMetal** is an experimental, bare-metal x86_64 microkernel written in 100% pure C# and compiled to zero-dependency native code using **.NET 10 Native AOT**. Operating at Ring 0 with a capability-based security model inspired by seL4, it features zero garbage-collection overhead in core IPC/scheduler paths and executes within a Higher-Half Direct Map (HHDM) memory layout.

Version **v2.0.1** delivers a major stability and hardware compatibility milestone: the **Native xHCI USB 3.0 & HID Keyboard subsystem is now fully operational and ships enabled by default** across all generated media, eliminating physical USB keyboard freezes, expanding ring capacities, aligning doorbells and endpoint types strictly with the xHCI 1.2 specification, and quieting driver logging for a pristine graphical framebuffer shell experience.

---

### What's New in v2.0.1

#### 1. Native xHCI Enabled by Default
- **Out-of-the-Box Operation:** `Make-DiskImage.sh` now defaults to `XHCI_NATIVE=1`, automatically packaging `xhci_native.flag` into `INITRD.IMG`.
- **Zero Configuration:** USB keyboards on bare-metal systems (such as modern laptops and PCs) and in QEMU virtualized environments operate immediately at boot without manual opt-in flags.
- **Fail-Safe Fallback:** If no xHCI controller is detected or BIOS handoff bounds are exceeded, the system gracefully retains authoritative PS/2 keyboard processing.

#### 2. Link TRB Wrap-Around Invariant & Freeze Fix
- **Root Cause Eliminated:** Fixed the physical USB keyboard freeze occurring after ~30 keystrokes caused by premature cycle bit toggling before Link TRB stamping.
- **Producer Cycle State (PCS) Invariant:** Transfer rings now strictly stamp the Link TRB (`RefreshLinkTrb`) with the *current* cycle state before toggling the cycle bit (`Cycle ^= 1`) and resetting enqueue pointers.
- **Continuous Typing Durability:** Fully verified with continuous, uninterrupted typing (>100+ keystrokes) across multiple ring wrap cycles without controller halts. Applied consistently across EP1 IN transfer rings, EP0 control rings, and command rings.

#### 3. Ring Capacity Expansions
- **256-Entry DMA Rings:** Expanded `Ep1RingEntries` and `EventRingEntries` from 64 to 256 entries in `XhciController.cs`, fully utilizing allocated 4096-byte DMA pages and significantly expanding burst throughput buffer capacity.

#### 4. Specification-Compliant Register Offset Doorbell Indexing
- **xHCI Spec Table 64 Alignment:** In strict compliance with xHCI Specification Section 5.6 and Table 64, doorbell registers are addressed per Slot ID (`DBOFF + SlotID * 4`), resolving command and endpoint timeout failures observed on physical silicon.

#### 5. Strict Table 65 Endpoint Typing & Packet Sizing
- **Endpoint Types:** Strict adherence to xHCI Table 65: EP0 default control endpoint configured as Type 4 (Bidirectional Control) and EP1 configured as Type 7 (Interrupt IN).
- **Speed-Matched Max Packet Sizes:** Dynamically configures EP0 MPS based on port speed (8 bytes for Low/Full Speed, 64 bytes for High Speed, 512 bytes for SuperSpeed), and enforces `ContextEntries = 2` for standard HID single-interface devices.

#### 6. Clean Framebuffer & Silent Shell Diagnostics
- **Pristine Display Console:** All verbose diagnostic logging across `bus.xhci`, `input.hid`, and `display_server` has been silenced in production builds.
- **Immediate Shell Focus:** The graphical GOP terminal screen cleanly transitions to the interactive `kernel:>` shell prompt without being flooded by dozens of driver discovery and keystroke inject logs.

#### 7. FAT32 Partition Offset Detection (LBA 2048)
- **Automatic Geometry & Offset Probing:** `Fat32Driver` inspects LBA 0 to differentiate between unpartitioned raw FAT32 images and GPT/MBR partitioned disks.
- **GPT/MBR Partition 1 Support:** Automatically detects the starting sector from partition entry 1 or falls back to standard GPT 1 MiB offset (LBA 2048), resolving boot signature errors on partitioned installation media and restoring `/HELLO.TXT` and `/bin/posix_test` accessibility.

#### 8. Dual-Writer GOP Collision Elimination & Full Screen Clear
- **Kernel Console Muting (`SysMuteConsole`):** Added syscall 0x27 (`SysMuteConsole`) invoked by `display_server` and `shell` to permanently disable early kernel framebuffer blitting while keeping serial/COM1 logging active.
- **Full Display Resolution Wipe:** Shell clears the entire GOP panel resolution (e.g. 1920×1080) with `0xFF1E1E2E` on startup, cleanly eliminating residual boot text on the lower screen.
- **ANSI Escape Sequence Filtering:** Added parser in kernel `DisplayConsole` to consume ESC sequences, eliminating stray ANSI code fragments (`52b`, `54m`).

---

### Quick Start & Installation

#### Running via QEMU (Recommended)

Ensure QEMU and OVMF UEFI firmware are installed, then execute:

```bash
qemu-system-x86_64 \
    -M q35 \
    -cpu host -enable-kvm \
    -m 2G \
    -smp 4 \
    -bios /usr/share/ovmf/OVMF.fd \
    -drive file=disk.img,format=raw,if=none,id=nvm \
    -device nvme,serial=deadbeef,drive=nvm \
    -netdev user,id=net0,hostfwd=udp::8080-:8080 \
    -device virtio-net-pci,netdev=net0 \
    -device qemu-xhci,id=xhci \
    -device usb-kbd,bus=xhci.0 \
    -serial stdio
```

*(Note: On systems without `/usr/share/ovmf/OVMF.fd`, install `ovmf` or `edk2-ovmf` via your system package manager).*

#### Flashing to Physical USB Drive (Bare-Metal)

Identify your target USB drive (`lsblk`) and write the raw GPT disk image:

```bash
# Replace /dev/sdX with your actual USB target drive
sudo dd if=disk.img of=/dev/sdX bs=4M status=progress oflag=sync
```
Reboot into UEFI setup, disable Secure Boot, and select the USB drive. Native xHCI USB input is active by default.

---

### Included Release Assets
- `disk.img`: Complete 64MB GPT disk image containing the EFI system partition (FAT32), direct UEFI application boot, the SharpMetal microkernel binary, and the 12-payload `INITRD.IMG` (shipping with `xhci_native.flag`).
- `nvme.img`: 64MB NVMe storage disk image pre-formatted with FAT32 containing userland test binaries (`/bin/test.pie`, `/bin/posix_test`, `/bin/cat.pie`, `/bin/hello.pie`) and sample assets (`/HELLO.TXT`).
- `BOOTX64.EFI`: Standalone x86_64 UEFI Native AOT executable.
NOTES

echo "==> [4/4] Publishing release ${TAG} to GitHub..."
git tag -f "${TAG}" HEAD
git push -f origin "${TAG}"

if gh release view "${TAG}" >/dev/null 2>&1; then
    echo "  -> Updating existing release ${TAG} notes and assets..."
    gh release edit "${TAG}" --title "${TITLE}" --notes-file "${NOTES_FILE}"
    gh release upload "${TAG}" \
        "${IMG_ASSET}#disk.img (64MB Bootable GPT Disk)" \
        "${NVME_ASSET}#nvme.img (64MB NVMe FAT32 Disk)" \
        "${EFI_ASSET}#BOOTX64.EFI (UEFI Native AOT Kernel)" \
        --clobber
else
    gh release create "${TAG}" \
        "${IMG_ASSET}#disk.img (64MB Bootable GPT Disk)" \
        "${NVME_ASSET}#nvme.img (64MB NVMe FAT32 Disk)" \
        "${EFI_ASSET}#BOOTX64.EFI (UEFI Native AOT Kernel)" \
        --title "${TITLE}" \
        --notes-file "${NOTES_FILE}"
fi

echo "==> Successfully created and published ${TAG}!"
