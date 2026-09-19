#!/usr/bin/env bash
# SharpMetal release publisher: uploads disk.img + BOOTX64.EFI as a GitHub release.
set -euo pipefail

TAG="${1:-v2.0.0}"
TITLE="SharpMetal ${TAG}: Hardened VMM, Frontiers 1–5 & POSIX Subsystem"

# Ensure GitHub CLI is available
command -v gh >/dev/null 2>&1 || { echo "ERROR: GitHub CLI (gh) is not installed or not in PATH."; exit 1; }

echo "==> [1/4] Checking git repository status..."
# Uncomment if you want automated remote synchronization prior to tagging:
# git pull --rebase origin main
# git push origin main

echo "==> [2/4] Locating release build artifacts..."
IMG_ASSET=$(find build/ -maxdepth 3 -type f -name "disk.img" | head -n 1)
EFI_ASSET=$(find build/ -maxdepth 5 -type f \( -name "BOOTX64.EFI" -o -name "bootx64.efi" \) | head -n 1)

if [ -z "${IMG_ASSET}" ] || [ ! -f "${IMG_ASSET}" ]; then
    echo "ERROR: disk.img not found under build/! Run: bash build/scripts/Make-DiskImage.sh"
    exit 1
fi

if [ -z "${EFI_ASSET}" ] || [ ! -f "${EFI_ASSET}" ]; then
    echo "ERROR: BOOTX64.EFI not found under build/! Run: bash build/scripts/Make-DiskImage.sh"
    exit 1
fi

echo "  -> Found Disk Image : ${IMG_ASSET} ($(du -h "${IMG_ASSET}" | cut -f1))"
echo "  -> Found EFI Binary : ${EFI_ASSET} ($(du -h "${EFI_ASSET}" | cut -f1))"

echo "==> [3/4] Compiling release notes..."
NOTES_FILE=$(mktemp /tmp/sharpmetal-release-notes.XXXXXX.md)
trap 'rm -f "${NOTES_FILE}"' EXIT

cat << 'NOTES' > "${NOTES_FILE}"
# SharpMetal v2.0.0: The Microkernel Frontier

**SharpMetal** is an experimental, bare-metal x86_64 microkernel written in 100% pure C# and compiled to zero-dependency native code using **.NET 10 Native AOT**. Operating at Ring 0 with a capability-based security model inspired by seL4, it features zero garbage-collection overhead in core IPC/scheduler paths and executes within a Higher-Half Direct Map (HHDM) memory layout.

Version **v2.0.0** introduces the **Frontiers 1–5 architectural hardening milestone**: a hardened VMM with demand paging and COW, dynamic ELF/PIE execution, a full Layer 2–4 network stack, a native USB 3.0 xHCI host driver with HID support, and a userland POSIX compatibility layer.

---

### What's New in v2.0.0

#### 1. Frontier 1: VMM & SMP Concurrency Hardening
- **Lock-Free COW Refcounting:** `PhysicalFrameRefcount` converted to atomic Compare-And-Swap (CAS) loops, eliminating spinlock contention during high-frequency Copy-On-Write page sharing and unsharing.
- **Inter-Processor TLB Shootdown:** `SmpTlbShootdown` integrated across all mutation paths—both during frame duplication and when single-reference pages are upgraded back to writable mode.
- **Canonical Address Boundary Enforcement:** The fault handler strictly enforces the `< 0x0000_8000_0000_0000` bound, immediately terminating unprivileged processes that attempt to probe the higher-half direct map (HHDM).

#### 2. Frontier 2: Dynamic ELF/PIE Relocation & Execution
- `Userland.PieLoader` gains a zero-alloc ELF64 parser (`Elf64_Ehdr`/`Phdr`/`Dyn`/`Rela`) with `PT_LOAD` segments registered as demand-paged, file-backed VMAs.
- `R_X86_64_RELATIVE` relocations applied against an ASLR base; kernel `SysSpawnElf` synthesizes the System V initial stack (argc/argv/envp + `AT_PHDR`/`AT_ENTRY`/`AT_RANDOM` auxv) and drops to Ring 3.

#### 3. Frontier 3: VirtIO Network Driver & Microkernel IP Stack (`net.stack`)
- **Layer 2/3/4:** Ethernet framing, ARP cache with expiration, IPv4 routing with checksum calculation, ICMP echo responder, UDP sockets, and a full TCP state machine (CLOSED through TIME_WAIT) with RTO retransmission.
- **POSIX-like Socket RPC:** `socket`, `bind`, `listen`, `accept`, `connect`, `send`, `recv`, and `close` exposed over the capability-secured `INetworkService` endpoint.
- **VirtIO-Net:** Split RX/TX virtqueues with zero-alloc DMA replenishment and interrupt-driven RX via `sys_recv_any`.

#### 4. Frontier 4: USB 3.0 xHCI Controller & HID Drivers (`bus.xhci`)
- **Native xHCI Subsystem:** Full hardware lifecycle management—DCBAA, Command/Event/Transfer rings, ERST, per-ring cycle-bit tracking, doorbell arrays, and scratchpad provisioning.
- **USB HID Boot Protocol:** Keyboard and mouse enumeration, 8-byte boot report translation via HID usage tables, and keys injected into `input.hid` via the `InjectKey` RPC.
- **Fail-Safe Legacy Handoff:** The `USBLEGSUP` ownership handshake is timeout-bounded; on timeout, the driver cleanly aborts without disabling BIOS legacy emulation, keeping the PS/2 input path functional. A bare-metal safety gate spawns `bus.xhci` only when `xhci_native.flag` is present in the initrd or a hypervisor is detected, keeping PS/2 authoritative on physical hardware unless explicitly opted in.

#### 5. Frontier 5: POSIX / Libc & WASI Compatibility Layer (`Microkernel.Posix`)
- **Syscall ABI Emulation:** Per-thread Linux ABI mode in the kernel dispatch table translates Linux x86_64 syscall numbers (`read`, `write`, `open`, `close`, `mmap`, `brk`, `socket`, `exit`) into SharpMetal capability RPCs.
- **FD Multiplexer:** FDs 0/1/2 route to the console/input service; FDs >= 3 route to FAT32/VFS handles or network sockets.
- **POSIX Host Runner:** `posix_runner` hosts statically linked PIE binaries in isolated Ring 3 capability domains.

#### 6. Freestanding Native AOT Runtime Stubs
- Standalone runtime stubs (`RhpAssignRef`, `RhpNewFast`, `RhpPInvoke`, `__security_cookie`) implemented across all modular userland drivers, stripping away unnecessary CoreLib dependencies.

#### 7. Build Pipeline & Concurrency Performance
- `Make-DiskImage.sh` packages 12 initrd payloads (11 zero-alloc service binaries plus the xHCI native opt-in flag) into `disk.img`.
- Removed `-maxcpucount:1` throttling, unlocking full multi-core Roslyn and NASM compilation with safe sequential project ordering.
- Added `Rebuild-KernelOnly.sh` for rapid kernel turnaround.

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
Reboot into UEFI setup, disable Secure Boot, and select the USB drive. On bare metal, the PS/2/legacy keyboard path remains authoritative unless `xhci_native.flag` is added to the initrd.

---

### Included Release Assets
- `disk.img`: Complete 64MB GPT disk image containing the EFI system partition (FAT32), direct UEFI application boot, the SharpMetal microkernel binary, and the 12-payload `INITRD.IMG`.
- `BOOTX64.EFI`: Standalone x86_64 UEFI Native AOT executable.
NOTES

echo "==> [4/4] Publishing release ${TAG} to GitHub..."
gh release create "${TAG}" \
    "${IMG_ASSET}#disk.img (64MB Bootable GPT Disk)" \
    "${EFI_ASSET}#BOOTX64.EFI (UEFI Native AOT Kernel)" \
    --title "${TITLE}" \
    --notes-file "${NOTES_FILE}"

echo "==> Successfully created and published ${TAG}!"
