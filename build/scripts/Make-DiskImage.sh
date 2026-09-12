#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
TFM="net10.0"

export PATH="${HOME}/.dotnet:${HOME}/.local/bin:${PATH}"
export LD_LIBRARY_PATH="${HOME}/.local/usr/lib64:${LD_LIBRARY_PATH:-}"

# Pre-flight dependency check for required toolchain binaries
REQUIRED_TOOLS=(dotnet nasm lld-link python3 dd mkfs.fat parted mformat mcopy mmd)
MISSING_TOOLS=()
for tool in "${REQUIRED_TOOLS[@]}"; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        MISSING_TOOLS+=("$tool")
    fi
done

if [ ${#MISSING_TOOLS[@]} -gt 0 ]; then
    echo "[-] Error: Missing required build tool(s): ${MISSING_TOOLS[*]}" >&2
    echo "[-] Please install them (e.g. sudo apt install nasm lld mtools parted dosfstools python3)." >&2
    exit 1
fi

TMP_DIR=$(mktemp -d /tmp/make_disk_XXXXXX)
cleanup() {
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT

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

echo "[BUILD] Compiling Userland.PieLoader IL..."
dotnet build "${REPO_ROOT}/src/runtime/Userland.PieLoader/Userland.PieLoader.csproj" -c Release

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

echo "[BUILD] Compiling net.virtio IL..."
dotnet build "${REPO_ROOT}/src/servers/drivers/net.virtio/net.virtio.csproj" -c Release

echo "[BUILD] Compiling fs.fat32 IL..."
dotnet build "${REPO_ROOT}/src/servers/fs.fat32/fs.fat32.csproj" -c Release

echo "[BUILD] Compiling Microkernel.Vfs IL..."
dotnet build "${REPO_ROOT}/src/common/Microkernel.Vfs/Microkernel.Vfs.csproj" -c Release

echo "[BUILD] Compiling shell IL..."
dotnet build "${REPO_ROOT}/src/apps/shell/shell.csproj" -c Release

echo "[BUILD] Compiling Kernel IL..."
dotnet build "${REPO_ROOT}/src/kernel/Kernel.csproj" -c Release

echo "=== [1/6] Locating Native AOT Compiler (ilc) ==="

# 1. Check if ILC is already set in environment and executable
if [ -z "${ILC:-}" ] || [ ! -x "${ILC:-}" ]; then
    # Search local NuGet package cache for .NET 10 ILCompiler first
    ILC=$(find "$HOME/.nuget/packages" -path "*/runtime.*.microsoft.dotnet.ilcompiler/10.*/tools/ilc" -type f -executable 2>/dev/null | sort -V | tail -n 1 || true)
    if [ -z "$ILC" ]; then
        ILC=$(find "$HOME/.nuget/packages" -path "*/tools/ilc" -type f -executable 2>/dev/null | sort -V | tail -n 1 || true)
    fi
fi

# 2. If not found, explicitly restore the compiler package into the NuGet cache
if [ -z "${ILC:-}" ] || [ ! -x "${ILC:-}" ]; then
    ARCH="$(uname -m)"
    OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
    case "$ARCH" in
        x86_64) ILC_ARCH="x64" ;;
        aarch64|arm64) ILC_ARCH="arm64" ;;
        *) ILC_ARCH="x64" ;;
    esac
    ILC_PKG="runtime.${OS}-${ILC_ARCH}.Microsoft.DotNet.ILCompiler"

    echo "[*] ilc not found in cache. Restoring ${ILC_PKG}..."
    TMP_RESTORE="${TMP_DIR}/ilc_restore"
    mkdir -p "$TMP_RESTORE"
    dotnet new console -o "$TMP_RESTORE" --no-restore >/dev/null 2>&1 || true
    if ! dotnet add "$TMP_RESTORE" package "$ILC_PKG" --package-directory "$HOME/.nuget/packages" >/dev/null 2>&1; then
        echo "[-] Fallback: Restoring runtime.linux-x64.Microsoft.DotNet.ILCompiler..." >&2
        dotnet add "$TMP_RESTORE" package runtime.linux-x64.Microsoft.DotNet.ILCompiler --package-directory "$HOME/.nuget/packages" >/dev/null 2>&1 || true
    fi
    rm -rf "$TMP_RESTORE"
    ILC=$(find "$HOME/.nuget/packages" -path "*/runtime.*.microsoft.dotnet.ilcompiler/10.*/tools/ilc" -type f -executable 2>/dev/null | sort -V | tail -n 1 || true)
    if [ -z "$ILC" ]; then
        ILC=$(find "$HOME/.nuget/packages" -path "*/tools/ilc" -type f -executable 2>/dev/null | sort -V | tail -n 1 || true)
    fi
fi

if [ -z "${ILC:-}" ] || [ ! -x "${ILC:-}" ]; then
    ILC=$(find "$HOME/.nuget/packages" -name "ilc" -type f 2>/dev/null | head -n 1 || true)
    if [ -n "${ILC:-}" ]; then
        chmod +x "$ILC" 2>/dev/null || true
    fi
fi

if [ -z "${ILC:-}" ] || [ ! -x "${ILC:-}" ]; then
    echo "[-] Error: Failed to locate executable 'ilc' Native AOT compiler binary." >&2
    exit 1
fi

echo "[AOT] Found Native AOT Compiler: $ILC"
export ILC
ILC_BIN="$ILC"
export ILC_BIN

echo "[AOT] Compiling roottask via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/${TFM}/roottask.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.Gc/bin/x64/Release/${TFM}/Userland.Runtime.Gc.dll" \
    -o "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/${TFM}/roottask.obj" \
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
    -o "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/${TFM}/RoottaskEntry.obj"

echo "[LINK] Linking roottask.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:RoottaskEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/roottask/bin/x64/Release/${TFM}/roottask.exe" \
    "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/${TFM}/RoottaskEntry.obj" \
    "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/${TFM}/roottask.obj"

cp "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/${TFM}/roottask.exe" \
   "${REPO_ROOT}/src/servers/roottask/bin/x64/Release/${TFM}/roottask.bin"

echo "[AOT] Compiling pci_server via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/${TFM}/pci_server.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/${TFM}/pci_server.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall

echo "[NASM] Assembling PciServerEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/pci_server/PciServerEntry.asm" \
    -o "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/${TFM}/PciServerEntry.obj"

echo "[LINK] Linking pci_server.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:PciServerEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/${TFM}/pci_server.exe" \
    "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/${TFM}/PciServerEntry.obj" \
    "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/${TFM}/pci_server.obj"

cp "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/${TFM}/pci_server.exe" \
   "${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/${TFM}/pci_server.bin"

echo "[AOT] Compiling display_server via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/${TFM}/display_server.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/${TFM}/display_server.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall \
    --directpinvoke:Avx2Blit \
    --directpinvoke:Avx2Fill

echo "[NASM] Assembling DisplayServerEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/display_server/DisplayServerEntry.asm" \
    -o "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/${TFM}/DisplayServerEntry.obj"

echo "[LINK] Linking display_server.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:DisplayServerEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/display_server/bin/x64/Release/${TFM}/display_server.exe" \
    "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/${TFM}/DisplayServerEntry.obj" \
    "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/${TFM}/display_server.obj"

cp "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/${TFM}/display_server.exe" \
   "${REPO_ROOT}/src/servers/display_server/bin/x64/Release/${TFM}/display_server.bin"

echo "[AOT] Compiling supervisor via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/${TFM}/supervisor.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/${TFM}/supervisor.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall

echo "[NASM] Assembling SupervisorEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/supervisor/SupervisorEntry.asm" \
    -o "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/${TFM}/SupervisorEntry.obj"

echo "[LINK] Linking supervisor.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:SupervisorEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/${TFM}/supervisor.exe" \
    "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/${TFM}/SupervisorEntry.obj" \
    "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/${TFM}/supervisor.obj"

cp "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/${TFM}/supervisor.exe" \
   "${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/${TFM}/supervisor.bin"

echo "[AOT] Compiling storage.nvme via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/storage.nvme.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/storage.nvme.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall

echo "[NASM] Assembling StorageNvmeEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/drivers/storage.nvme/StorageNvmeEntry.asm" \
    -o "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/StorageNvmeEntry.obj"

echo "[LINK] Linking storage.nvme.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:StorageNvmeEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/storage.nvme.exe" \
    "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/StorageNvmeEntry.obj" \
    "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/storage.nvme.obj"

cp "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/storage.nvme.exe" \
   "${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/storage.nvme.bin"

echo "[AOT] Compiling input.hid via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/input.hid.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/input.hid.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall \
    --directpinvoke:PortIn8 \
    --directpinvoke:PortOut8

echo "[NASM] Assembling InputHidEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/drivers/input.hid/InputHidEntry.asm" \
    -o "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/InputHidEntry.obj"

echo "[LINK] Linking input.hid.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:InputHidEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/input.hid.exe" \
    "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/InputHidEntry.obj" \
    "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/input.hid.obj"

cp "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/input.hid.exe" \
   "${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/input.hid.bin"

echo "[AOT] Compiling net.virtio via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/net.virtio.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/net.virtio.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall

echo "[NASM] Assembling VirtioNetEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/drivers/net.virtio/VirtioNetEntry.asm" \
    -o "${REPO_ROOT}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/VirtioNetEntry.obj"

echo "[LINK] Linking net.virtio.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:VirtioNetEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/net.virtio.exe" \
    "${REPO_ROOT}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/VirtioNetEntry.obj" \
    "${REPO_ROOT}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/net.virtio.obj"

cp "${REPO_ROOT}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/net.virtio.exe" \
   "${REPO_ROOT}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/net.virtio.bin"

echo "[AOT] Compiling fs.fat32 via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/servers/fs.fat32/bin/x64/Release/${TFM}/fs.fat32.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" \
    -o "${REPO_ROOT}/src/servers/fs.fat32/bin/x64/Release/${TFM}/fs.fat32.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall

echo "[NASM] Assembling Fat32Entry.asm..."
nasm -f win64 "${REPO_ROOT}/src/servers/fs.fat32/Fat32Entry.asm" \
    -o "${REPO_ROOT}/src/servers/fs.fat32/bin/x64/Release/${TFM}/Fat32Entry.obj"

echo "[LINK] Linking fs.fat32.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:Fat32Entry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/servers/fs.fat32/bin/x64/Release/${TFM}/fs.fat32.exe" \
    "${REPO_ROOT}/src/servers/fs.fat32/bin/x64/Release/${TFM}/Fat32Entry.obj" \
    "${REPO_ROOT}/src/servers/fs.fat32/bin/x64/Release/${TFM}/fs.fat32.obj"

cp "${REPO_ROOT}/src/servers/fs.fat32/bin/x64/Release/${TFM}/fs.fat32.exe" \
   "${REPO_ROOT}/src/servers/fs.fat32/bin/x64/Release/${TFM}/fs.fat32.bin"

echo "[BUILD] Userland.PieLoader (pre-shell AOT guard)..."
dotnet build "${REPO_ROOT}/src/runtime/Userland.PieLoader/Userland.PieLoader.csproj" -c Release --nologo -v q
# Canonicalize: bare `dotnet build` emits bin/Release/<TFM>/, while x64-platform
# builds emit bin/x64/Release/<TFM>/. Shell ilc -r below uses the flat path,
# so mirror the fresh x64 DLL there to avoid stale/missing reference warnings.
mkdir -p "${REPO_ROOT}/src/runtime/Userland.PieLoader/bin/Release/${TFM}"
cp -f "${REPO_ROOT}/src/runtime/Userland.PieLoader/bin/x64/Release/${TFM}/Userland.PieLoader.dll" \
      "${REPO_ROOT}/src/runtime/Userland.PieLoader/bin/Release/${TFM}/Userland.PieLoader.dll" 2>/dev/null || true

echo "[AOT] Compiling shell via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/apps/shell/bin/x64/Release/${TFM}/shell.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.Runtime.Gc/bin/x64/Release/${TFM}/Userland.Runtime.Gc.dll" \
    -r "${REPO_ROOT}/src/libs/Microkernel.Drawing/bin/x64/Release/${TFM}/Microkernel.Drawing.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Vfs/bin/Release/${TFM}/Microkernel.Vfs.dll" \
    -r "${REPO_ROOT}/src/runtime/Userland.PieLoader/bin/Release/${TFM}/Userland.PieLoader.dll" \
    -o "${REPO_ROOT}/src/apps/shell/bin/x64/Release/${TFM}/shell.obj" \
    --targetos windows \
    --targetarch x64 \
    --systemmodule MiniCoreLib \
    --nativelib \
    --directpinvoke:Syscall \
    --directpinvoke:GetFontGlyphs

echo "[NASM] Assembling ShellEntry.asm..."
nasm -f win64 -i"${REPO_ROOT}/" "${REPO_ROOT}/src/apps/shell/ShellEntry.asm" \
    -o "${REPO_ROOT}/src/apps/shell/bin/x64/Release/${TFM}/ShellEntry.obj"

echo "[LINK] Linking shell.exe via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:console \
    /entry:ShellEntry \
    /base:0x40000000 \
    /out:"${REPO_ROOT}/src/apps/shell/bin/x64/Release/${TFM}/shell.exe" \
    "${REPO_ROOT}/src/apps/shell/bin/x64/Release/${TFM}/ShellEntry.obj" \
    "${REPO_ROOT}/src/apps/shell/bin/x64/Release/${TFM}/shell.obj"

cp "${REPO_ROOT}/src/apps/shell/bin/x64/Release/${TFM}/shell.exe" \
   "${REPO_ROOT}/src/apps/shell/bin/x64/Release/${TFM}/shell.bin"

echo "[AOT] Compiling Kernel via Native AOT (ilc)..."
"$ILC" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/Kernel.dll" \
    -r "${REPO_ROOT}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" \
    -r "${REPO_ROOT}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/Kernel.obj" \
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
    --directpinvoke:XSetBv \
    --directpinvoke:Invlpg \
    --directpinvoke:ReadRflags \
    --directpinvoke:RestoreRflags \
    --directpinvoke:CpuPause \
    --directpinvoke:AtomicIncrement32 \
    --directpinvoke:AtomicDecrement32 \
    --directpinvoke:AtomicCompareExchange32 \
    --directpinvoke:AtomicCompareExchange64 \
    --directpinvoke:AtomicExchange32 \
    --directpinvoke:AtomicFetchAndAdd32 \
    --directpinvoke:GetCurrentCoreIndex \
    --directpinvoke:GetCurrentThread \
    --directpinvoke:SetCurrentThread \
    --directpinvoke:GetApEntry64 \
    --directpinvoke:SetApInitialStack \
    --directpinvoke:GetApTrampolineBinary \
    --directpinvoke:GetApTrampolineBinarySize

echo "[NASM] Assembling ApTrampoline.asm..."
nasm -f bin "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/ApTrampoline.asm" \
    -o "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/ApTrampoline.bin"

echo "[NASM] Assembling Entry.asm..."
nasm -f win64 -i"${REPO_ROOT}/" "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/Entry.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/Entry.obj"

echo "[NASM] Assembling DescriptorFlush.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/DescriptorFlush.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/DescriptorFlush.obj"

echo "[NASM] Assembling IsrTrampolines.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/IsrTrampolines.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/IsrTrampolines.obj"

echo "[NASM] Assembling ContextSwitch.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/ContextSwitch.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/ContextSwitch.obj"

echo "[NASM] Assembling SyscallEntry.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/SyscallEntry.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/SyscallEntry.obj"

echo "[NASM] Assembling UserTransition.asm..."
nasm -f win64 "${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly/UserTransition.asm" \
    -o "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/UserTransition.obj"

echo "[LINK] Linking BOOTX64.EFI via lld-link..."
lld-link \
    /align:4096 \
    /filealign:4096 \
    /nodefaultlib \
    /subsystem:efi_application \
    /entry:EfiMain \
    /out:"${REPO_ROOT}/build/BOOTX64.EFI" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/Kernel.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/Entry.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/DescriptorFlush.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/IsrTrampolines.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/ContextSwitch.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/SyscallEntry.obj" \
    "${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}/UserTransition.obj"

echo "[STAGE] Staging EFI System Partition directory..."
ESP_DIR="${REPO_ROOT}/build/esp"
mkdir -p "${ESP_DIR}/EFI/BOOT"
cp "${REPO_ROOT}/build/BOOTX64.EFI" "${ESP_DIR}/EFI/BOOT/BOOTX64.EFI"
echo '\EFI\BOOT\BOOTX64.EFI' > "${ESP_DIR}/startup.nsh"

echo "[PACK] Packaging initial ramdisk (INITRD.IMG)..."
python3 "${REPO_ROOT}/build/scripts/Pack-Initrd.py" \
    "${ESP_DIR}/EFI/BOOT/INITRD.IMG" \
    roottask="${REPO_ROOT}/src/servers/roottask/bin/x64/Release/${TFM}/roottask.bin" \
    pci_server.bin="${REPO_ROOT}/src/servers/pci_server/bin/x64/Release/${TFM}/pci_server.bin" \
    display_server.bin="${REPO_ROOT}/src/servers/display_server/bin/x64/Release/${TFM}/display_server.bin" \
    supervisor.bin="${REPO_ROOT}/src/servers/supervisor/bin/x64/Release/${TFM}/supervisor.bin" \
    storage.nvme.bin="${REPO_ROOT}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/storage.nvme.bin" \
    input.hid.bin="${REPO_ROOT}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/input.hid.bin" \
    net.virtio.bin="${REPO_ROOT}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/net.virtio.bin" \
    fs.fat32.bin="${REPO_ROOT}/src/servers/fs.fat32/bin/x64/Release/${TFM}/fs.fat32.bin" \
    shell.bin="${REPO_ROOT}/src/apps/shell/bin/x64/Release/${TFM}/shell.bin"

# Constraint 1: Rootless FAT32 Disk Staging
NVME_IMG="${REPO_ROOT}/build/nvme.img"
echo "[DISK] Creating 64MB rootless FAT32 NVMe disk image at ${NVME_IMG}..."
rm -f "${NVME_IMG}"
dd if=/dev/zero of="${NVME_IMG}" bs=1M count=64 status=none
mkfs.fat -F 32 -s 1 "${NVME_IMG}"
TMP_HELLO="${TMP_DIR}/HELLO.TXT"
printf "SharpMetal BareMetal OS" > "${TMP_HELLO}"
mcopy -i "${NVME_IMG}" "${TMP_HELLO}" ::/HELLO.TXT

# Build raw GPT disk image with FAT32 ESP
DISK_IMG="${REPO_ROOT}/build/disk.img"
echo "[DISK] Creating 64MB GPT disk image with FAT32 ESP..."
rm -f "${DISK_IMG}"
dd if=/dev/zero of="${DISK_IMG}" bs=1M count=64 status=none

PART_START_SECTORS=2048
PART_END_SECTORS=131038
PART_SECTORS=$((PART_END_SECTORS - PART_START_SECTORS + 1))
PART_START_BYTES=$((PART_START_SECTORS * 512))

parted -s "${DISK_IMG}" mklabel gpt
parted -s "${DISK_IMG}" mkpart ESP fat32 "${PART_START_SECTORS}s" "${PART_END_SECTORS}s"
parted -s "${DISK_IMG}" set 1 esp on

# Format partition using mformat with exact sector count to protect secondary GPT
mformat -i "${DISK_IMG}"@@${PART_START_BYTES} -T "${PART_SECTORS}" -F -v "EFI_SYSTEM"
mmd -i "${DISK_IMG}"@@${PART_START_BYTES} ::EFI
mmd -i "${DISK_IMG}"@@${PART_START_BYTES} ::EFI/BOOT
mcopy -i "${DISK_IMG}"@@${PART_START_BYTES} "${REPO_ROOT}/build/BOOTX64.EFI" ::EFI/BOOT/BOOTX64.EFI
mcopy -i "${DISK_IMG}"@@${PART_START_BYTES} "${ESP_DIR}/EFI/BOOT/INITRD.IMG" ::EFI/BOOT/INITRD.IMG
mcopy -i "${DISK_IMG}"@@${PART_START_BYTES} "${ESP_DIR}/startup.nsh" ::startup.nsh
echo "[DISK] Disk image created successfully at ${DISK_IMG}"

echo "[SUCCESS] Make-DiskImage completed successfully."
