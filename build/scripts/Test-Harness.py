#!/usr/bin/env python3
import subprocess
import sys
import os

def find_ovmf():
    candidates = [
        # Ubuntu / Debian CI
        ("/usr/share/OVMF/OVMF_CODE.fd", "/usr/share/OVMF/OVMF_VARS.fd"),
        ("/usr/share/ovmf/OVMF.fd", None),
        # Fedora / RHEL
        ("/usr/share/edk2/ovmf/OVMF_CODE.fd", "/usr/share/edk2/ovmf/OVMF_VARS.fd"),
        # Local user override
        (os.path.expanduser("~/.local/usr/share/edk2/ovmf/OVMF_CODE.fd"),
         os.path.expanduser("~/.local/usr/share/edk2/ovmf/OVMF_VARS.fd")),
    ]
    for code, vars_file in candidates:
        if os.path.exists(code):
            return code, vars_file
    raise FileNotFoundError("Could not find valid OVMF firmware image.")

def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.abspath(os.path.join(script_dir, "../.."))

    print("=================================================================")
    print("  SharpMetal C# Microkernel: Automated Test Harness (Phase 10)   ")
    print("=================================================================")

    # Probe OVMF
    code_fd, vars_fd = find_ovmf()
    print(f"[OVMF] Verified firmware image at: {code_fd}")

    # Step 1: Run Make-DiskImage.sh
    print("\n[STEP 1] Running Make-DiskImage.sh...")
    make_disk = os.path.join(script_dir, "Make-DiskImage.sh")
    res = subprocess.run(["bash", make_disk], cwd=repo_root, capture_output=True, text=True)
    if res.returncode != 0:
        print("[FAIL] Make-DiskImage.sh failed!")
        print("STDOUT:\n", res.stdout)
        print("STDERR:\n", res.stderr)
        sys.exit(1)
    print("[PASS] Kernel built, drivers packaged, and disk image staged successfully.")

    # Step 2: Run QEMU via Run-Qemu.sh
    print("\n[STEP 2] Running QEMU test under OVMF with NVMe storage and VirtIO-Net...")
    run_qemu = os.path.join(script_dir, "Run-Qemu.sh")
    nvme_img = os.path.join(repo_root, "build/nvme.img")

    try:
        proc = subprocess.run(
            [
                "bash", run_qemu,
                "-drive", f"file={nvme_img},format=raw,if=none,id=nvm",
                "-device", "nvme,serial=nvme01,drive=nvm",
                "-netdev", "user,id=net0",
                "-device", "virtio-net-pci,netdev=net0"
            ],
            cwd=repo_root,
            capture_output=True,
            text=True,
            timeout=8
        )
        exit_code = proc.returncode
        output = proc.stdout + "\n" + proc.stderr
    except subprocess.TimeoutExpired as e:
        print("[FAIL] QEMU timed out after 8 seconds!")
        if e.stdout:
            print("STDOUT:\n", e.stdout.decode(errors='replace'))
        if e.stderr:
            print("STDERR:\n", e.stderr.decode(errors='replace'))
        sys.exit(1)

    print(f"[QEMU] Exit code: {exit_code}")
    print("\n--- Serial Output Begin ---")
    print(output.strip())
    print("--- Serial Output End ---\n")

    # Step 3: Validate exit code
    # isa-debug-exit on port 0xF4 with code 0x10 returns exit code (0x10 << 1) | 1 = 33
    expected_exit = 33
    if exit_code != expected_exit:
        print(f"[FAIL] Expected QEMU exit code {expected_exit}, but got {exit_code}")
        sys.exit(1)
    print(f"[PASS] QEMU exited with expected code {expected_exit} (0x10 via isa-debug-exit).")

    # Step 4: Validate Phase 10 required banners and tokens in serial output
    required_tokens = [
        "[NVME] Controller initialized. Admin and I/O queues online.",
        "[FAT32] Volume mounted. Found root directory entry: HELLO.TXT",
        "[VFS] File.ReadAllText('/HELLO.TXT') -> \"SharpMetal BareMetal OS\"",
        "[VIRTIO] VirtIO-Net controller online. MAC:",
        "[SHELL] History ring buffer initialized (32 slots).",
        "[DISPLAY] AVX2 compositor blitted alpha-blended surface.",
        "[SUCCESS] Phase 10 fully operational. Exiting QEMU..."
    ]

    for token in required_tokens:
        if token not in output:
            print(f"[FAIL] Missing required token in serial log: '{token}'")
            sys.exit(1)
        print(f"[PASS] Found required token: '{token}'")

    print("\n=================================================================")
    print("   ALL PHASE 10 VERIFICATION TESTS PASSED SUCCESSFULLY!         ")
    print("=================================================================")
    sys.exit(0)

if __name__ == "__main__":
    main()
