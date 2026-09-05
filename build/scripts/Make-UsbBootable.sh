#!/usr/bin/env bash
set -e

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD_DIR="${REPO_ROOT}/build"

print_usage() {
    echo "Usage: $0 <block_device> [--i-know-what-i-am-doing]"
    echo ""
    echo "Safely prepares a bootable UEFI USB drive for SharpMetal C# Microkernel."
    echo ""
    echo "Available block devices on host:"
    lsblk -o NAME,SIZE,TYPE,TRAN,RM,MODEL 2>/dev/null || true
    echo ""
    echo "Example: sudo $0 /dev/sdb"
}

if [ -z "$1" ] || [ "$1" = "-h" ] || [ "$1" = "--help" ]; then
    print_usage
    exit 1
fi

TARGET="$1"
OVERRIDE_FLAG="$2"

# 1. Validate block device existence
if [ ! -b "$TARGET" ]; then
    echo "[-] ERROR: Target '$TARGET' is not a valid block device!" >&2
    print_usage
    exit 1
fi

# 2. Check for required build artifacts
if [ ! -f "${BUILD_DIR}/BOOTX64.EFI" ] || [ ! -f "${BUILD_DIR}/esp/EFI/BOOT/INITRD.IMG" ] || [ ! -f "${BUILD_DIR}/nvme.img" ]; then
    echo "[-] ERROR: Missing build artifacts. Please run Make-DiskImage.sh first!" >&2
    exit 1
fi

# 3. Check removable media attribute
DEV_NAME="$(basename "$TARGET")"
RM_VAL="$(lsblk -ndo RM "$TARGET" 2>/dev/null || cat "/sys/block/${DEV_NAME}/removable" 2>/dev/null || echo "0")"

if [ "$RM_VAL" != "1" ]; then
    if [ "$OVERRIDE_FLAG" != "--i-know-what-i-am-doing" ]; then
        echo "[-] ERROR: Device '$TARGET' is NOT marked as removable (RM=$RM_VAL)!" >&2
        echo "[-] To prevent accidental system drive overwrites, non-removable drives are blocked." >&2
        echo "[-] If you are positive, pass '--i-know-what-i-am-doing' as the second parameter." >&2
        exit 1
    fi
    echo "[!] WARNING: Overriding non-removable device protection as requested!"
fi

# 4. Check that target is NOT currently mounted as root (/) or boot (/boot)
if findmnt -n -o TARGET "$TARGET" 2>/dev/null | grep -qE '^/(boot)?$'; then
    echo "[-] ERROR: Refusing to write to system drive ($TARGET is mounted as / or /boot)!" >&2
    exit 1
fi

for part_mount in $(lsblk -nlo MOUNTPOINT "$TARGET" 2>/dev/null); do
    if echo "$part_mount" | grep -qE '^/(boot)?$'; then
        echo "[-] ERROR: Refusing to write to system drive (partition on $TARGET is mounted at '$part_mount')!" >&2
        exit 1
    fi
done

# 5. Display target information and require explicit confirmation
echo "================================================================="
echo "       SharpMetal Microkernel Bare-Metal USB Flashing Tool       "
echo "================================================================="
echo "TARGET DEVICE DETAILS:"
lsblk -do NAME,SIZE,MODEL,TRAN,RM "$TARGET" 2>/dev/null || lsblk "$TARGET"
echo ""
echo "WARNING: ALL DATA ON '$TARGET' WILL BE PERMANENTLY ERASED!"
echo "Type 'YES' in capital letters to proceed:"
read -r CONFIRMATION
if [ "$CONFIRMATION" != "YES" ]; then
    echo "[-] Aborted by user."
    exit 1
fi

# 6. Unmount any active partitions on the target drive
echo "[*] Unmounting active partitions on $TARGET..."
for p in $(lsblk -nlo PATH "$TARGET" 2>/dev/null | tail -n +2); do
    umount "$p" 2>/dev/null || true
done
umount "${TARGET}"* 2>/dev/null || true

# 7. Wipe partition signatures
echo "[*] Wiping partition table signatures..."
wipefs -a "$TARGET" || true

# 8. Create GPT partition table with a single 128 MiB EFI System Partition
echo "[*] Creating GPT partition table with 128 MiB EFI System Partition..."
parted -s "$TARGET" mklabel gpt mkpart "EFI" fat32 1MiB 128MiB set 1 esp on

# 9. Mandatory Adjustment 3: USB Partition Settling
echo "[*] Settling partition table changes (partprobe && udevadm settle)..."
partprobe "$TARGET" && udevadm settle

# Check both ${TARGET}1 and ${TARGET}p1
PART=""
if [ -b "${TARGET}1" ]; then
    PART="${TARGET}1"
elif [ -b "${TARGET}p1" ]; then
    PART="${TARGET}p1"
else
    sleep 1
    partprobe "$TARGET" 2>/dev/null || true
    udevadm settle 2>/dev/null || true
    if [ -b "${TARGET}1" ]; then
        PART="${TARGET}1"
    elif [ -b "${TARGET}p1" ]; then
        PART="${TARGET}p1"
    else
        echo "[-] ERROR: Could not find partition ${TARGET}1 or ${TARGET}p1 after parted!" >&2
        exit 1
    fi
fi
echo "[+] Detected partition: $PART"

# 10. Format partition as FAT32
echo "[*] Formatting $PART as FAT32 (SHARPMETAL)..."
mkfs.vfat -F 32 -n "SHARPMETAL" "$PART"

# 11. Mount temporary directory and stage bootloader & ramdisk
echo "[*] Staging bootloader and microkernel payloads..."
TMP_MNT=$(mktemp -d /tmp/sharpmetal_usb_XXXXXX)
mount "$PART" "$TMP_MNT"

mkdir -p "$TMP_MNT/EFI/BOOT"
cp "${BUILD_DIR}/BOOTX64.EFI" "$TMP_MNT/EFI/BOOT/BOOTX64.EFI"
cp "${BUILD_DIR}/esp/EFI/BOOT/INITRD.IMG" "$TMP_MNT/EFI/BOOT/INITRD.IMG"
cp "${BUILD_DIR}/nvme.img" "$TMP_MNT/NVME.IMG"
if [ -f "${BUILD_DIR}/esp/startup.nsh" ]; then
    cp "${BUILD_DIR}/esp/startup.nsh" "$TMP_MNT/startup.nsh"
fi

sync
umount "$TMP_MNT"
rmdir "$TMP_MNT"

echo ""
echo "================================================================="
echo "   USB FLASHING COMPLETED SUCCESSFULLY: $TARGET is bootable!    "
echo "================================================================="
echo "Bare-Metal Boot Instructions:"
echo " 1. Insert this USB drive into your physical x86-64 target PC."
echo " 2. Enter UEFI Firmware Settings (BIOS) and DISABLE Secure Boot."
echo " 3. Ensure UEFI Boot Mode (not Legacy / CSM) is enabled."
echo " 4. Open the UEFI Boot Menu (F12 / F11 / F8 / Del / Esc at power on)."
echo " 5. Select 'UEFI: [USB Drive Name]' / 'SHARPMETAL'."
echo " 6. SharpMetal Native AOT EFI bootloader will initialize and boot!"
echo "================================================================="
