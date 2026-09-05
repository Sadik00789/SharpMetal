#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

export PATH="${HOME}/.dotnet:${HOME}/.local/bin:${PATH}"
export LD_LIBRARY_PATH="${HOME}/.local/usr/lib64:${LD_LIBRARY_PATH:-}"

echo "[BUILD] Compiling MiniCoreLib..."
dotnet build "${REPO_ROOT}/src/common/MiniCoreLib/MiniCoreLib.csproj" -c Release

echo "[BUILD] Compiling Microkernel.Abstractions..."
dotnet build "${REPO_ROOT}/src/common/Microkernel.Abstractions/Microkernel.Abstractions.csproj" -c Release

echo "[BUILD] Compiling Microkernel.RpcGenerator..."
dotnet build "${REPO_ROOT}/src/compiler-plugins/Microkernel.RpcGenerator/Microkernel.RpcGenerator.csproj" -c Release

echo "[BUILD] Compiling Userland.Runtime.ZeroAlloc..."
dotnet build "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/Userland.Runtime.ZeroAlloc.csproj" -c Release

echo "[BUILD] Compiling Userland.Runtime.Gc..."
dotnet build "${REPO_ROOT}/src/runtime/Userland.Runtime.Gc/Userland.Runtime.Gc.csproj" -c Release

echo "[BUILD] Compiling roottask IL..."
dotnet build "${REPO_ROOT}/src/servers/roottask/roottask.csproj" -c Release

echo "[BUILD] Compiling pci_server IL..."
dotnet build "${REPO_ROOT}/src/servers/pci_server/pci_server.csproj" -c Release

echo "[BUILD] Compiling display_server IL..."
dotnet build "${REPO_ROOT}/src/servers/display_server/display_server.csproj" -c Release

echo "[BUILD] Compiling supervisor IL..."
dotnet build "${REPO_ROOT}/src/servers/supervisor/supervisor.csproj" -c Release

echo "[BUILD] Compiling Microkernel.Drawing IL..."
dotnet build "${REPO_ROOT}/src/libs/Microkernel.Drawing/Microkernel.Drawing.csproj" -c Release

echo "[BUILD] Compiling storage.nvme IL..."
dotnet build "${REPO_ROOT}/src/servers/drivers/storage.nvme/storage.nvme.csproj" -c Release

echo "[BUILD] Compiling input.hid IL..."
dotnet build "${REPO_ROOT}/src/servers/drivers/input.hid/input.hid.csproj" -c Release

echo "[BUILD] Compiling shell IL..."
dotnet build "${REPO_ROOT}/src/apps/shell/shell.csproj" -c Release

echo "[BUILD] Compiling Kernel IL..."
dotnet build "${REPO_ROOT}/src/kernel/Kernel.csproj" -c Release

ILC_BIN="${HOME}/.nuget/packages/runtime.linux-x64.microsoft.dotnet.ilcompiler/9.0.19/tools/ilc"
if [[ ! -f "${ILC_BIN}" ]]; then
    ILC_BIN=$(find "${HOME}/.nuget/packages" -name "ilc" -type f -perm -111 2>/dev/null | head -n 1)
fi

echo "[AOT] Compiling roottask via Native AOT (ilc)..."
"${ILC_BIN}" \
    "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/net9.0/roottask.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/net9.0/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/net9.0/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/net9.0/Userland.Runtime.ZeroAlloc.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.Gc/bin/x64/Release/net9.0/Userland.Runtime.Gc.dll" \
    -o "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/net9.0/roottask.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall \
    --directpinvoke:GetCs \
    --directpinvoke:GetRsp \
    --directpinvoke:CaptureCalleeSavedRegisters

echo "[NASM] Assembling RoottaskEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/roottask/RoottaskEntry.asm" \
    -o "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/net9.0/RoottaskEntry.obj"

echo "[LINK] Linking roottask.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:RoottaskEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/roottask/bin/x64/Release/net9.0/roottask.exe" \
    "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/net9.0/RoottaskEntry.obj" \
    "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/net9.0/roottask.obj"

cp "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/net9.0/roottask.exe" \
   "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/net9.0/roottask.bin"

echo "[AOT] Compiling pci_server via Native AOT (ilc)..."
"${ILC_BIN}" \
    "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/net9.0/pci_server.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/net9.0/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/net9.0/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/net9.0/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/net9.0/pci_server.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall

echo "[NASM] Assembling PciServerEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/pci_server/PciServerEntry.asm" \
    -o "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/net9.0/PciServerEntry.obj"

echo "[LINK] Linking pci_server.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:PciServerEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/net9.0/pci_server.exe" \
    "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/net9.0/PciServerEntry.obj" \
    "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/net9.0/pci_server.obj"

cp "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/net9.0/pci_server.exe" \
   "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/net9.0/pci_server.bin"

echo "[AOT] Compiling display_server via Native AOT (ilc)..."
"${ILC_BIN}" \
    "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/net9.0/display_server.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/net9.0/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/net9.0/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/net9.0/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/net9.0/display_server.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall \
    --directpinvoke:Avx2Blit \
    --directpinvoke:Avx2Fill

echo "[NASM] Assembling DisplayServerEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/display_server/DisplayServerEntry.asm" \
    -o "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/net9.0/DisplayServerEntry.obj"

echo "[LINK] Linking display_server.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:DisplayServerEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/display_server/bin/x64/Release/net9.0/display_server.exe" \
    "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/net9.0/DisplayServerEntry.obj" \
    "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/net9.0/display_server.obj"

cp "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/net9.0/display_server.exe" \
   "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/net9.0/display_server.bin"

echo "[AOT] Compiling supervisor via Native AOT (ilc)..."
"${ILC_BIN}" \
    "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/net9.0/supervisor.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/net9.0/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/net9.0/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/net9.0/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/net9.0/supervisor.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall

echo "[NASM] Assembling SupervisorEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/supervisor/SupervisorEntry.asm" \
    -o "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/net9.0/SupervisorEntry.obj"

echo "[LINK] Linking supervisor.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:SupervisorEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/net9.0/supervisor.exe" \
    "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/net9.0/SupervisorEntry.obj" \
    "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/net9.0/supervisor.obj"

cp "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/net9.0/supervisor.exe" \
   "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/net9.0/supervisor.bin"

echo "[AOT] Compiling storage.nvme via Native AOT (ilc)..."
"${ILC_BIN}" \
    "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/net9.0/storage.nvme.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/net9.0/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/net9.0/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/net9.0/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/net9.0/storage.nvme.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall

echo "[NASM] Assembling StorageNvmeEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/drivers/storage.nvme/StorageNvmeEntry.asm" \
    -o "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/net9.0/StorageNvmeEntry.obj"

echo "[LINK] Linking storage.nvme.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:StorageNvmeEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/net9.0/storage.nvme.exe" \
    "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/net9.0/StorageNvmeEntry.obj" \
    "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/net9.0/storage.nvme.obj"

cp "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/net9.0/storage.nvme.exe" \
   "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/net9.0/storage.nvme.bin"

echo "[AOT] Compiling input.hid via Native AOT (ilc)..."
"${ILC_BIN}" \
    "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/net9.0/input.hid.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/net9.0/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/net9.0/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/net9.0/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/net9.0/input.hid.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall \
    --directpinvoke:PortIn8 \
    --directpinvoke:PortOut8

echo "[NASM] Assembling InputHidEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/drivers/input.hid/InputHidEntry.asm" \
    -o "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/net9.0/InputHidEntry.obj"

echo "[LINK] Linking input.hid.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:InputHidEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/net9.0/input.hid.exe" \
    "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/net9.0/InputHidEntry.obj" \
    "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/net9.0/input.hid.obj"

cp "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/net9.0/input.hid.exe" \
   "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/net9.0/input.hid.bin"

echo "[AOT] Compiling shell via Native AOT (ilc)..."
"${ILC_BIN}" \
    "${REPO_ROOT}/src/apps/shell/bin/x64/Release/net9.0/shell.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/net9.0/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/net9.0/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/net9.0/Userland.Runtime.ZeroAlloc.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.Gc/bin/x64/Release/net9.0/Userland.Runtime.Gc.dll" \
    -r "${REPO_ROOT}/src/libs/Microkernel.Drawing/bin/x64/Release/net9.0/Microkernel.Drawing.dll" \
    -o "${REPO_ROOT}/src/apps/shell/bin/x64/Release/net9.0/shell.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall \
    --directpinvoke:GetFontGlyphs

echo "[NASM] Assembling ShellEntry.asm..."
nasm -f win64 -i"${REPO_ROOT}/" "${REPO_ROOT}/src/apps/shell/ShellEntry.asm" \
    -o "${REPO_ROOT}/src/apps/shell/bin/x64/Release/net9.0/ShellEntry.obj"

echo "[LINK] Linking shell.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:ShellEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/apps/shell/bin/x64/Release/net9.0/shell.exe" \
    "${REPO_ROOT}/src/apps/shell/bin/x64/Release/net9.0/ShellEntry.obj" \
    "${REPO_ROOT}/src/apps/shell/bin/x64/Release/net9.0/shell.obj"

cp "${REPO_ROOT}/src/apps/shell/bin/x64/Release/net9.0/shell.exe" \
   "${REPO_ROOT}/src/apps/shell/bin/x64/Release/net9.0/shell.bin"

echo "[AOT] Compiling Kernel via Native AOT (ilc)..."
"${ILC_BIN}" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/Kernel.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/net9.0/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/net9.0/Microkernel.Abstractions.dll" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/Kernel.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Out8 \
    --directpinvoke:In8 \
    --directpinvoke:Out16 \
    --directpinvoke:In16 \
    --directpinvoke:Out32 \
    --directpinvoke:In32 \
    --directpinvoke:IoWait \
    --directpinvoke:ReadCr0 \
    --directpinvoke:WriteCr0 \
    --directpinvoke:ReadCr3 \
    --directpinvoke:ReadCr2 \
    --directpinvoke:WriteCr3 \
    --directpinvoke:ReadCr4 \
    --directpinvoke:WriteCr4 \
    --directpinvoke:ReadMsr \
    --directpinvoke:WriteMsr \
    --directpinvoke:DisableInterrupts \
    --directpinvoke:EnableInterrupts \
    --directpinvoke:GetRsp \
    --directpinvoke:GetRip \
    --directpinvoke:SwitchToHigherHalf \
    --directpinvoke:LoadGdt \
    --directpinvoke:ReloadSegments \
    --directpinvoke:LoadTss \
    --directpinvoke:LoadIdt \
    --directpinvoke:GetIsrThunkTable \
    --directpinvoke:ContextSwitch \
    --directpinvoke:DoSyscall \
    --directpinvoke:GetSyscallEntry \
    --directpinvoke:GetThreadStartTrampoline \
    --directpinvoke:EnterUserMode \
    --directpinvoke:SetSyscallKernelRsp \
    --directpinvoke:GetUserThreadTrampoline \
    --directpinvoke:XSetBv

echo "[NASM] Assembling Entry.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/Entry.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/Entry.obj"

echo "[NASM] Assembling DescriptorFlush.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/DescriptorFlush.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/DescriptorFlush.obj"

echo "[NASM] Assembling IsrTrampolines.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/IsrTrampolines.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/IsrTrampolines.obj"

echo "[NASM] Assembling ContextSwitch.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/ContextSwitch.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/ContextSwitch.obj"

echo "[NASM] Assembling SyscallEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/SyscallEntry.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/SyscallEntry.obj"

echo "[NASM] Assembling UserTransition.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/UserTransition.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/UserTransition.obj"

echo "[LINK] Linking BOOTX64.EFI via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:efi_application \
    /entry:EfiMain \
    /out:"${REPO_ROOT}/build/BOOTX64.EFI" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/Kernel.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/Entry.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/DescriptorFlush.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/IsrTrampolines.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/ContextSwitch.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/SyscallEntry.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/net9.0/UserTransition.obj"

echo "[STAGE] Staging EFI System Partition directory..."
ESP_DIR="${REPO_ROOT}/build/esp"
mkdir -p "${ESP_DIR}/EFI/BOOT"
cp "${REPO_ROOT}/build/BOOTX64.EFI" "${ESP_DIR}/EFI/BOOT/BOOTX64.EFI"
echo '\EFI\BOOT\BOOTX64.EFI' > "${ESP_DIR}/startup.nsh"

echo "[PACK] Packaging initial ramdisk (INITRD.IMG)..."
python3 "${REPO_ROOT}/build/scripts/Pack-Initrd.py" \
    "${ESP_DIR}/EFI/BOOT/INITRD.IMG" \
    roottask="${REPO_ROOT}/src/servers/roottask/bin/x64/Release/net9.0/roottask.bin" \
    pci_server.bin="${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/net9.0/pci_server.bin" \
    display_server.bin="${REPO_ROOT}/src/servers/display_server/bin/x64/Release/net9.0/display_server.bin" \
    supervisor.bin="${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/net9.0/supervisor.bin" \
    storage.nvme.bin="${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/net9.0/storage.nvme.bin" \
    input.hid.bin="${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/net9.0/input.hid.bin" \
    shell.bin="${REPO_ROOT}/src/apps/shell/bin/x64/Release/net9.0/shell.bin"

# Constraint 4: Create 32MB raw NVMe image
NVME_IMG="${REPO_ROOT}/build/nvme.img"
if [[ ! -f "${NVME_IMG}" ]]; then
    echo "[DISK] Creating 32MB raw NVMe disk image at ${NVME_IMG}..."
    dd if=/dev/zero of="${NVME_IMG}" bs=1M count=32 status=none
fi

# Build raw GPT disk image if parted and mtools are available
DISK_IMG="${REPO_ROOT}/build/disk.img"
if command -v parted >/dev/null 2>&1 && command -v mformat >/dev/null 2>&1 && command -v mcopy >/dev/null 2>&1; then
    echo "[DISK] Creating 64MB GPT disk image with FAT32 ESP..."
    rm -f "${DISK_IMG}"
    dd if=/dev/zero of="${DISK_IMG}" bs=1M count=64 status=none
    parted -s "${DISK_IMG}" mklabel gpt
    parted -s "${DISK_IMG}" mkpart ESP fat32 2048s 131038s
    parted -s "${DISK_IMG}" set 1 esp on

    # Format partition using mformat with offset
    PART_START=$((2048 * 512))
    mformat -i "${DISK_IMG}"@@${PART_START} -F -v "EFI_SYSTEM"
    mmd -i "${DISK_IMG}"@@${PART_START} ::EFI
    mmd -i "${DISK_IMG}"@@${PART_START} ::EFI/BOOT
    mcopy -i "${DISK_IMG}"@@${PART_START} "${REPO_ROOT}/build/BOOTX64.EFI" ::EFI/BOOT/BOOTX64.EFI
    mcopy -i "${DISK_IMG}"@@${PART_START} "${ESP_DIR}/EFI/BOOT/INITRD.IMG" ::EFI/BOOT/INITRD.IMG
    mcopy -i "${DISK_IMG}"@@${PART_START} "${ESP_DIR}/startup.nsh" ::startup.nsh
    echo "[DISK] Disk image created successfully at ${DISK_IMG}"
fi

echo "[SUCCESS] Make-DiskImage completed successfully."
