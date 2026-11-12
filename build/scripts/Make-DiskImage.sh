#!/usr/bin/env bash
set -euo pipefail

BUILD_START_SECS=$SECONDS

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
TFM="net10.0"

export PATH="${HOME}/.dotnet:${HOME}/.local/bin:${PATH}"
export LD_LIBRARY_PATH="${HOME}/.local/usr/lib64:${LD_LIBRARY_PATH:-}"

# Pre-flight dependency check for required toolchain binaries
REQUIRED_TOOLS=(dotnet nasm lld-link python3 dd mkfs.fat parted mformat mcopy mmd gcc)
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

OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
ARCH="$(uname -m)"
IS_WINDOWS_BASH=0
if [[ "$OS" == mingw* || "$OS" == msys* || "$OS" == cygwin* ]] && command -v cygpath >/dev/null 2>&1; then
    IS_WINDOWS_BASH=1
fi

host_path() {
    if [ "$IS_WINDOWS_BASH" -eq 1 ]; then
        cygpath -m "$1"
    else
        printf '%s\n' "$1"
    fi
}

REPO_HOST="$(host_path "$REPO_ROOT")"
REPO_MSBUILD="$REPO_HOST"

echo "=== [0/6] Restoring and Building Managed IL Graph in Parallel ==="
T_IL_START=$SECONDS
BUILD_PROJ="${TMP_DIR}/SharpMetalBuild.proj"
cat > "$BUILD_PROJ" <<EOF
<Project>
  <PropertyGroup>
    <RepoRoot>${REPO_MSBUILD}</RepoRoot>
  </PropertyGroup>
  <ItemGroup>
    <SharpMetalProj Include="\$(RepoRoot)/src/common/MiniCoreLib/MiniCoreLib.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/common/Microkernel.Abstractions/Microkernel.Abstractions.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/compiler-plugins/Microkernel.RpcGenerator/Microkernel.RpcGenerator.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/runtime/Userland.Runtime.ZeroAlloc/Userland.Runtime.ZeroAlloc.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/runtime/Userland.Runtime.Gc/Userland.Runtime.Gc.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/common/Microkernel.Vfs/Microkernel.Vfs.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/runtime/Userland.PieLoader/Userland.PieLoader.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/libs/Microkernel.Drawing/Microkernel.Drawing.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/net.stack/net.stack.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/libs/Microkernel.Posix/Microkernel.Posix.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/roottask/roottask.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/pci_server/pci_server.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/display_server/display_server.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/supervisor/supervisor.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/drivers/storage.nvme/storage.nvme.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/drivers/input.hid/input.hid.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/drivers/bus.xhci/bus.xhci.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/drivers/net.virtio/net.virtio.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/servers/fs.fat32/fs.fat32.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/apps/shell/shell.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/apps/posix_runner/posix_runner.csproj" />
    <SharpMetalProj Include="\$(RepoRoot)/src/kernel/Kernel.csproj" />
  </ItemGroup>
  <Target Name="Restore">
    <MSBuild Projects="@(SharpMetalProj)" Targets="Restore" BuildInParallel="true" />
  </Target>
  <Target Name="Build">
    <MSBuild Projects="@(SharpMetalProj)" Targets="Build" BuildInParallel="true" Properties="Configuration=Release" />
  </Target>
</Project>
EOF

BUILD_PROJ_HOST="$(host_path "$BUILD_PROJ")"
echo "[BUILD] Restoring solution packages..."
dotnet restore "$BUILD_PROJ_HOST" --nologo -m
echo "[BUILD] Compiling all 22 projects in parallel..."
dotnet msbuild "$BUILD_PROJ_HOST" -t:Build -m --nologo

# Mirror AnyCPU vs x64 Release DLLs where ilc references expect canonical paths
for pair in \
    "src/common/Microkernel.Vfs:Microkernel.Vfs.dll" \
    "src/runtime/Userland.PieLoader:Userland.PieLoader.dll" \
    "src/servers/net.stack:net.stack.dll"
do
    dir="${pair%%:*}"
    dll="${pair#*:}"
    mkdir -p "${REPO_ROOT}/${dir}/bin/Release/${TFM}"
    mkdir -p "${REPO_ROOT}/${dir}/bin/x64/Release/${TFM}"
    if [ -f "${REPO_ROOT}/${dir}/bin/x64/Release/${TFM}/${dll}" ]; then
        cp -f "${REPO_ROOT}/${dir}/bin/x64/Release/${TFM}/${dll}" "${REPO_ROOT}/${dir}/bin/Release/${TFM}/${dll}"
    elif [ -f "${REPO_ROOT}/${dir}/bin/Release/${TFM}/${dll}" ]; then
        cp -f "${REPO_ROOT}/${dir}/bin/Release/${TFM}/${dll}" "${REPO_ROOT}/${dir}/bin/x64/Release/${TFM}/${dll}"
    fi
done
echo "[BUILD] Managed IL build completed in $((SECONDS - T_IL_START))s"

echo "=== [1/6] Locating Native AOT Compiler (ilc) ==="

case "$ARCH" in
    x86_64|amd64) ILC_ARCH="x64" ;;
    aarch64|arm64) ILC_ARCH="arm64" ;;
    *) ILC_ARCH="x64" ;;
esac
case "$OS" in
    mingw*|msys*|cygwin*) ILC_OS="win"; ILC_NAME="ilc.exe" ;;
    darwin*) ILC_OS="osx"; ILC_NAME="ilc" ;;
    *) ILC_OS="linux"; ILC_NAME="ilc" ;;
esac

find_ilc() {
    local found=""
    found=$(find "$HOME/.nuget/packages" -path "*/runtime.${ILC_OS}-${ILC_ARCH}.microsoft.dotnet.ilcompiler/10.*/tools/${ILC_NAME}" -type f 2>/dev/null | sort -V | tail -n 1 || true)
    if [ -z "$found" ]; then
        found=$(find "$HOME/.nuget/packages" -path "*/runtime.${ILC_OS}-*.microsoft.dotnet.ilcompiler/*/tools/${ILC_NAME}" -type f 2>/dev/null | sort -V | tail -n 1 || true)
    fi
    if [ -n "$found" ] && [ ! -x "$found" ]; then
        chmod +x "$found" 2>/dev/null || true
    fi
    printf '%s\n' "$found"
}

if [ -z "${ILC:-}" ] || [ ! -x "${ILC:-}" ]; then
    ILC="$(find_ilc)"
fi

if [ -z "${ILC:-}" ] || [ ! -x "${ILC:-}" ]; then
    ILC_RID="${ILC_OS}-${ILC_ARCH}"
    ILC_PKG="runtime.${ILC_RID}.Microsoft.DotNet.ILCompiler"

    echo "[*] ${ILC_NAME} not found in cache. Restoring ${ILC_PKG}..."
    TMP_RESTORE="${TMP_DIR}/ilc_restore"
    mkdir -p "$TMP_RESTORE"
    TMP_RESTORE_HOST="$(host_path "$TMP_RESTORE")"
    PKG_DIR_HOST="$(host_path "$HOME/.nuget/packages")"
    dotnet new console -o "$TMP_RESTORE_HOST" --no-restore >/dev/null 2>&1 || true
    NUGET_SRC_ARGS=(--source https://api.nuget.org/v3/index.json)
    [ -f "${REPO_ROOT}/NuGet.Config" ] && NUGET_SRC_ARGS=(--configfile "$(host_path "${REPO_ROOT}/NuGet.Config")")
    if dotnet add "$TMP_RESTORE_HOST" package Microsoft.DotNet.ILCompiler --version "10.0.*" --no-restore >/dev/null 2>&1; then
        dotnet restore "$TMP_RESTORE_HOST" -r "$ILC_RID" "${NUGET_SRC_ARGS[@]}" --packages "$PKG_DIR_HOST" >/dev/null 2>&1 || true
    fi
    if [ -z "$(find_ilc)" ]; then
        if ! dotnet add "$TMP_RESTORE_HOST" package "$ILC_PKG" "${NUGET_SRC_ARGS[@]}" --package-directory "$PKG_DIR_HOST" >/dev/null 2>&1; then
            echo "[-] Fallback: Restoring runtime.linux-x64.Microsoft.DotNet.ILCompiler..." >&2
            dotnet add "$TMP_RESTORE_HOST" package runtime.linux-x64.Microsoft.DotNet.ILCompiler "${NUGET_SRC_ARGS[@]}" --package-directory "$PKG_DIR_HOST" >/dev/null 2>&1 || true
        fi
    fi
    rm -rf "$TMP_RESTORE"
    ILC="$(find_ilc)"
fi

if [ -z "${ILC:-}" ] || [ ! -x "${ILC:-}" ]; then
    echo "[-] Error: Failed to locate executable '${ILC_NAME}' Native AOT compiler binary." >&2
    exit 1
fi

echo "[AOT] Found Native AOT Compiler: $ILC"
export ILC
ILC_BIN="$ILC"
export ILC_BIN

ref_dll_path() {
    case "$1" in
        MiniCoreLib)                printf '%s\n' "${REPO_HOST}/src/common/MiniCoreLib/bin/Release/${TFM}/MiniCoreLib.dll" ;;
        Microkernel.Abstractions)   printf '%s\n' "${REPO_HOST}/src/common/Microkernel.Abstractions/bin/x64/Release/${TFM}/Microkernel.Abstractions.dll" ;;
        Userland.Runtime.ZeroAlloc) printf '%s\n' "${REPO_HOST}/src/runtime/Userland.Runtime.ZeroAlloc/bin/x64/Release/${TFM}/Userland.Runtime.ZeroAlloc.dll" ;;
        Userland.Runtime.Gc)        printf '%s\n' "${REPO_HOST}/src/runtime/Userland.Runtime.Gc/bin/x64/Release/${TFM}/Userland.Runtime.Gc.dll" ;;
        Microkernel.Vfs)            printf '%s\n' "${REPO_HOST}/src/common/Microkernel.Vfs/bin/Release/${TFM}/Microkernel.Vfs.dll" ;;
        Userland.PieLoader)         printf '%s\n' "${REPO_HOST}/src/runtime/Userland.PieLoader/bin/Release/${TFM}/Userland.PieLoader.dll" ;;
        Microkernel.Drawing)        printf '%s\n' "${REPO_HOST}/src/libs/Microkernel.Drawing/bin/x64/Release/${TFM}/Microkernel.Drawing.dll" ;;
        Microkernel.Posix)          printf '%s\n' "${REPO_HOST}/src/libs/Microkernel.Posix/bin/x64/Release/${TFM}/Microkernel.Posix.dll" ;;
        net.stack)                  printf '%s\n' "${REPO_HOST}/src/servers/net.stack/bin/x64/Release/${TFM}/net.stack.dll" ;;
        *) echo "[-] Unknown reference key: $1" >&2; return 1 ;;
    esac
}

build_userland_target() {
    local name="$1"
    local rel_dir="$2"
    local asm_file="$3"
    local entry_sym="$4"
    local base_addr="$5"
    local refs_csv="$6"
    local pinvokes_csv="$7"
    local extra_nasm="${8:-}"

    local out_dir="${REPO_ROOT}/${rel_dir}/bin/x64/Release/${TFM}"
    local out_dir_host
    out_dir_host="$(host_path "$out_dir")"
    local asm_obj_host="${out_dir_host}/${asm_file%.asm}.obj"
    local asm_src_host
    asm_src_host="$(host_path "${REPO_ROOT}/${rel_dir}/${asm_file}")"

    local ilc_args=(
        "${out_dir_host}/${name}.dll"
    )
    local IFS=','
    for r in $refs_csv; do
        ilc_args+=(-r "$(ref_dll_path "$r")")
    done
    ilc_args+=(
        -o "${out_dir_host}/${name}.obj"
        --targetos windows
        --targetarch x64
        --systemmodule MiniCoreLib
        --nativelib
    )
    for p in $pinvokes_csv; do
        ilc_args+=("--directpinvoke:${p}")
    done
    unset IFS

    echo "[AOT] Compiling ${name} via Native AOT (ilc)..."
    "$ILC" "${ilc_args[@]}"

    echo "[NASM] Assembling ${asm_file}..."
    if [ -n "$extra_nasm" ]; then
        nasm -f win64 "$extra_nasm" "$asm_src_host" -o "$asm_obj_host"
    else
        nasm -f win64 "$asm_src_host" -o "$asm_obj_host"
    fi

    echo "[LINK] Linking ${name}.exe via lld-link..."
    lld-link \
        /align:4096 \
        /filealign:4096 \
        /nodefaultlib \
        /subsystem:console \
        /entry:"${entry_sym}" \
        /base:"${base_addr}" \
        /out:"${out_dir_host}/${name}.exe" \
        "$asm_obj_host" \
        "${out_dir_host}/${name}.obj"

    cp "${out_dir}/${name}.exe" "${out_dir}/${name}.bin"
}

build_kernel_target() {
    local kern_bin="${REPO_ROOT}/src/kernel/bin/x64/Release/${TFM}"
    local kern_bin_host
    kern_bin_host="$(host_path "$kern_bin")"
    local asm_dir="${REPO_ROOT}/src/kernel/Arch/x86_64/Assembly"
    local asm_dir_host
    asm_dir_host="$(host_path "$asm_dir")"

    echo "[NASM] Assembling ApTrampoline.asm..."
    nasm -f bin "${asm_dir_host}/ApTrampoline.asm" -o "${asm_dir_host}/ApTrampoline.bin"

    echo "[AOT] Compiling Kernel via Native AOT (ilc)..."
    "$ILC" \
        "${kern_bin_host}/Kernel.dll" \
        -r "$(ref_dll_path MiniCoreLib)" \
        -r "$(ref_dll_path Microkernel.Abstractions)" \
        -o "${kern_bin_host}/Kernel.obj" \
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
        --directpinvoke:DropToUser \
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
        --directpinvoke:GetApTrampolineBinarySize \
        --directpinvoke:GetKernelFontGlyphs

    echo "[NASM] Assembling Kernel assembly stubs..."
    nasm -f win64 -i"${REPO_HOST}/" "${asm_dir_host}/Entry.asm" -o "${kern_bin_host}/Entry.obj"
    for stub in DescriptorFlush IsrTrampolines ContextSwitch SyscallEntry UserTransition; do
        nasm -f win64 "${asm_dir_host}/${stub}.asm" -o "${kern_bin_host}/${stub}.obj"
    done

    echo "[LINK] Linking BOOTX64.EFI via lld-link..."
    lld-link \
        /align:4096 \
        /filealign:4096 \
        /nodefaultlib \
        /subsystem:efi_application \
        /entry:EfiMain \
        /out:"${REPO_HOST}/build/BOOTX64.EFI" \
        "${kern_bin_host}/Kernel.obj" \
        "${kern_bin_host}/Entry.obj" \
        "${kern_bin_host}/DescriptorFlush.obj" \
        "${kern_bin_host}/IsrTrampolines.obj" \
        "${kern_bin_host}/ContextSwitch.obj" \
        "${kern_bin_host}/SyscallEntry.obj" \
        "${kern_bin_host}/UserTransition.obj"
}

build_pie_c() {
    local label="$1"
    local src="$2"
    local out="$3"
    echo "[ELF] Compiling freestanding static PIE ${label}..."
    gcc -fPIE -pie -nostdlib -Wl,--no-dynamic-linker -Wl,-e,_start -Wl,-z,noseparate-code -o "$(host_path "$out")" "$(host_path "${REPO_ROOT}/${src}")"
}

JOB_LOGS_DIR="${TMP_DIR}/job_logs"
mkdir -p "$JOB_LOGS_DIR"
JOB_PIDS=()
JOB_NAMES=()
JOB_LOGS=()

MAX_JOBS="${SHARPMETAL_JOBS:-$(nproc 2>/dev/null || echo "${NUMBER_OF_PROCESSORS:-4}")}"
[ "$MAX_JOBS" -lt 1 ] && MAX_JOBS=1

launch_job() {
    local job_name="$1"
    shift
    while [ "$(jobs -rp | wc -l)" -ge "$MAX_JOBS" ]; do
        wait -n 2>/dev/null || sleep 0.1
    done
    local log_file="${JOB_LOGS_DIR}/${job_name}.log"
    (
        set -euo pipefail
        "$@"
    ) >"$log_file" 2>&1 &
    JOB_PIDS+=($!)
    JOB_NAMES+=("$job_name")
    JOB_LOGS+=("$log_file")
}

wait_all_jobs() {
    local failed=0
    local i
    for i in "${!JOB_PIDS[@]}"; do
        if wait "${JOB_PIDS[$i]}"; then
            cat "${JOB_LOGS[$i]}"
        else
            echo "[-] Job '${JOB_NAMES[$i]}' failed:" >&2
            cat "${JOB_LOGS[$i]}" >&2
            failed=1
        fi
    done
    JOB_PIDS=()
    JOB_NAMES=()
    JOB_LOGS=()
    [ "$failed" -eq 0 ] || exit 1
}

echo "=== [2/6] Compiling Native AOT, Kernel, and Static-PIE Targets (up to ${MAX_JOBS} parallel jobs) ==="
T_AOT_START=$SECONDS

TMP_ELF="${TMP_DIR}/test.pie"
TMP_HELLO_PIE="${TMP_DIR}/hello.pie"
TMP_CAT_PIE="${TMP_DIR}/cat.pie"

launch_job Kernel build_kernel_target
launch_job roottask build_userland_target roottask src/servers/roottask RoottaskEntry.asm RoottaskEntry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc,Userland.Runtime.Gc,Microkernel.Vfs,Userland.PieLoader" \
    "Syscall,GetCs,GetRsp,CaptureCalleeSavedRegisters"
launch_job shell build_userland_target shell src/apps/shell ShellEntry.asm ShellEntry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc,Userland.Runtime.Gc,Microkernel.Drawing,Microkernel.Vfs,Userland.PieLoader" \
    "Syscall,GetFontGlyphs" "-i${REPO_HOST}/"
launch_job posix_runner build_userland_target posix_runner src/apps/posix_runner Crt0.asm PosixRunnerEntry 0x20000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc,Userland.Runtime.Gc,Microkernel.Vfs,Userland.PieLoader,Microkernel.Posix" \
    "Syscall"
launch_job pci_server build_userland_target pci_server src/servers/pci_server PciServerEntry.asm PciServerEntry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc" "Syscall"
launch_job display_server build_userland_target display_server src/servers/display_server DisplayServerEntry.asm DisplayServerEntry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc" "Syscall,Avx2Blit,Avx2Fill"
launch_job supervisor build_userland_target supervisor src/servers/supervisor SupervisorEntry.asm SupervisorEntry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc" "Syscall"
launch_job storage.nvme build_userland_target storage.nvme src/servers/drivers/storage.nvme StorageNvmeEntry.asm StorageNvmeEntry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc" "Syscall"
launch_job input.hid build_userland_target input.hid src/servers/drivers/input.hid InputHidEntry.asm InputHidEntry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc" "Syscall,PortIn8,PortOut8"
launch_job bus.xhci build_userland_target bus.xhci src/servers/drivers/bus.xhci XhciEntry.asm XhciEntry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc" "Syscall,Mfence"
launch_job net.virtio build_userland_target net.virtio src/servers/drivers/net.virtio VirtioNetEntry.asm VirtioNetEntry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc,net.stack" "Syscall"
launch_job fs.fat32 build_userland_target fs.fat32 src/servers/fs.fat32 Fat32Entry.asm Fat32Entry 0x40000000 \
    "MiniCoreLib,Microkernel.Abstractions,Userland.Runtime.ZeroAlloc" "Syscall"
launch_job pie_elftest build_pie_c "ElfTest.c" "src/apps/frontier_tests/ElfTest.c" "$TMP_ELF"
launch_job pie_hello build_pie_c "hello_world.c" "src/apps/frontier_tests/hello_world.c" "$TMP_HELLO_PIE"
launch_job pie_cat build_pie_c "cat.c" "src/apps/frontier_tests/cat.c" "$TMP_CAT_PIE"

wait_all_jobs
echo "[AOT] Native AOT, Kernel, NASM, Link, and PIE builds completed in $((SECONDS - T_AOT_START))s"

echo "=== [3/6] Staging ESP Directory and Packing INITRD.IMG ==="
ESP_DIR="${REPO_ROOT}/build/esp"
mkdir -p "${ESP_DIR}/EFI/BOOT"
cp "${REPO_ROOT}/build/BOOTX64.EFI" "${ESP_DIR}/EFI/BOOT/BOOTX64.EFI"
echo '\EFI\BOOT\BOOTX64.EFI' > "${ESP_DIR}/startup.nsh"

echo "[PACK] Packaging initial ramdisk (INITRD.IMG)..."
# Native xHCI support: roottask spawns bus.xhci when this entry is present (or
# when running under a hypervisor). Enabled by default (XHCI_NATIVE=1); can be
# disabled via XHCI_NATIVE=0 for BIOS legacy PS/2 fallback.
XHCI_FLAG_ARGS=()
if [ "${XHCI_NATIVE:-1}" = "1" ]; then
    : > "${REPO_ROOT}/build/xhci_native.flag"
    XHCI_FLAG_ARGS+=("xhci_native.flag=${REPO_HOST}/build/xhci_native.flag")
    echo "[BUILD] xhci_native.flag staged into INITRD"
else
    rm -f "${REPO_ROOT}/build/xhci_native.flag"
    echo "[PACK] Native xHCI opt-in disabled: not packing xhci_native.flag"
fi
python3 "${REPO_HOST}/build/scripts/Pack-Initrd.py" \
    "${REPO_HOST}/build/esp/EFI/BOOT/INITRD.IMG" \
    roottask="${REPO_HOST}/src/servers/roottask/bin/x64/Release/${TFM}/roottask.bin" \
    pci_server.bin="${REPO_HOST}/src/servers/pci_server/bin/x64/Release/${TFM}/pci_server.bin" \
    display_server.bin="${REPO_HOST}/src/servers/display_server/bin/x64/Release/${TFM}/display_server.bin" \
    supervisor.bin="${REPO_HOST}/src/servers/supervisor/bin/x64/Release/${TFM}/supervisor.bin" \
    storage.nvme.bin="${REPO_HOST}/src/servers/drivers/storage.nvme/bin/x64/Release/${TFM}/storage.nvme.bin" \
    input.hid.bin="${REPO_HOST}/src/servers/drivers/input.hid/bin/x64/Release/${TFM}/input.hid.bin" \
    bus.xhci.bin="${REPO_HOST}/src/servers/drivers/bus.xhci/bin/x64/Release/${TFM}/bus.xhci.bin" \
    ${XHCI_FLAG_ARGS[@]+"${XHCI_FLAG_ARGS[@]}"} \
    net.virtio.bin="${REPO_HOST}/src/servers/drivers/net.virtio/bin/x64/Release/${TFM}/net.virtio.bin" \
    fs.fat32.bin="${REPO_HOST}/src/servers/fs.fat32/bin/x64/Release/${TFM}/fs.fat32.bin" \
    shell.bin="${REPO_HOST}/src/apps/shell/bin/x64/Release/${TFM}/shell.bin" \
    posix_runner.bin="${REPO_HOST}/src/apps/posix_runner/bin/x64/Release/${TFM}/posix_runner.bin"

if [ "${XHCI_NATIVE:-1}" = "1" ]; then
    python3 "${REPO_HOST}/build/scripts/Pack-Initrd.py" --verify "${REPO_HOST}/build/esp/EFI/BOOT/INITRD.IMG" "xhci_native.flag"
fi

echo "=== [4/6] Staging FAT32 NVMe and GPT EFI Disk Images in Parallel ==="
T_DISK_START=$SECONDS
TMP_HELLO="${TMP_DIR}/HELLO.TXT"
printf "SharpMetal BareMetal OS" > "${TMP_HELLO}"

stage_pie_binaries() {
    local target="$1"
    mmd -i "$target" ::/bin || true
    mcopy -i "$target" "${TMP_ELF}" ::/bin/test.pie
    mcopy -i "$target" "${TMP_ELF}" ::/bin/elftest.pie
    mcopy -i "$target" "${TMP_ELF}" ::/test.pie
    mcopy -i "$target" "${TMP_HELLO_PIE}" ::/bin/hello.pie
    mcopy -i "$target" "${TMP_HELLO_PIE}" ::/bin/posix.pie
    mcopy -i "$target" "${TMP_HELLO_PIE}" ::/bin/posix_test
    mcopy -i "$target" "${TMP_HELLO_PIE}" ::/hello.pie
    mcopy -i "$target" "${TMP_HELLO_PIE}" ::/posix.pie
    mcopy -i "$target" "${TMP_HELLO_PIE}" ::/posix_test
    mcopy -i "$target" "${TMP_CAT_PIE}" ::/bin/cat.pie
    mcopy -i "$target" "${TMP_CAT_PIE}" ::/cat.pie
    mcopy -i "$target" "${REPO_ROOT}/src/apps/posix_runner/bin/x64/Release/${TFM}/posix_runner.bin" ::/bin/posix_runner || true
    mcopy -i "$target" "${REPO_ROOT}/src/apps/posix_runner/bin/x64/Release/${TFM}/posix_runner.bin" ::/posix_runner.bin || true
}

build_nvme_image() {
    local nvme_img="${REPO_ROOT}/build/nvme.img"
    echo "[DISK] Creating 64MB rootless FAT32 NVMe disk image at ${nvme_img}..."
    rm -f "${nvme_img}"
    dd if=/dev/zero of="${nvme_img}" bs=1M count=64 status=none
    mkfs.fat -F 32 -s 1 "${nvme_img}"
    mcopy -i "${nvme_img}" "${TMP_HELLO}" ::/HELLO.TXT
    stage_pie_binaries "${nvme_img}"
}

build_gpt_disk_image() {
    local disk_img="${REPO_ROOT}/build/disk.img"
    echo "[DISK] Creating 64MB GPT disk image with FAT32 ESP..."
    rm -f "${disk_img}"
    dd if=/dev/zero of="${disk_img}" bs=1M count=64 status=none

    local part_start_sectors=2048
    local part_end_sectors=131038
    local part_sectors=$((part_end_sectors - part_start_sectors + 1))
    local part_start_bytes=$((part_start_sectors * 512))

    parted -s "${disk_img}" mklabel gpt
    parted -s "${disk_img}" mkpart ESP fat32 "${part_start_sectors}s" "${part_end_sectors}s"
    parted -s "${disk_img}" set 1 esp on

    mformat -i "${disk_img}"@@${part_start_bytes} -T "${part_sectors}" -F -v "EFI_SYSTEM"
    mmd -i "${disk_img}"@@${part_start_bytes} ::EFI
    mmd -i "${disk_img}"@@${part_start_bytes} ::EFI/BOOT
    mcopy -i "${disk_img}"@@${part_start_bytes} "${REPO_ROOT}/build/BOOTX64.EFI" ::EFI/BOOT/BOOTX64.EFI
    mcopy -i "${disk_img}"@@${part_start_bytes} "${ESP_DIR}/EFI/BOOT/INITRD.IMG" ::EFI/BOOT/INITRD.IMG
    mcopy -i "${disk_img}"@@${part_start_bytes} "${ESP_DIR}/startup.nsh" ::startup.nsh
    mcopy -i "${disk_img}"@@${part_start_bytes} "${TMP_HELLO}" ::/HELLO.TXT
    stage_pie_binaries "${disk_img}"@@${part_start_bytes}
    echo "[DISK] Disk image created successfully at ${disk_img}"
}

launch_job nvme_img build_nvme_image
launch_job gpt_disk_img build_gpt_disk_image
wait_all_jobs
echo "[DISK] Disk image staging completed in $((SECONDS - T_DISK_START))s"

echo "[SUCCESS] Make-DiskImage completed successfully in $((SECONDS - BUILD_START_SECS))s."
