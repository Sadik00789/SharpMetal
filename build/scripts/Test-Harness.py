#!/usr/bin/env python3
import subprocess
import sys
import os

def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.abspath(os.path.join(script_dir, "../.."))

    print("=================================================================")
    print("   Bare-Metal C# Microkernel: Automated Test Harness (Phase 9)   ")
    print("=================================================================")

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

    # Step 2: Run QEMU via Run-Qemu.sh with NVMe drive flags (Constraint 4)
    print("\n[STEP 2] Running QEMU test under OVMF with NVMe storage...")
    run_qemu = os.path.join(script_dir, "Run-Qemu.sh")
    nvme_img = os.path.join(repo_root, "build/nvme.img")

    try:
        proc = subprocess.run(
            [
                "bash", run_qemu,
                "-drive", f"file={nvme_img},format=raw,if=none,id=nvm",
                "-device", "nvme,serial=nvme01,drive=nvm"
            ],
            cwd=repo_root,
            capture_output=True,
            text=True,
            timeout=8
        )
        exit_code = proc.returncode
        output = proc.stdout + "\n" + proc.stderr
    except subprocess.TimeoutExpired as e:
        print("[FAIL] QEMU timed out after 15 seconds!")
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

    # Step 4: Validate Phase 9 required banners and tokens in serial output
    required_tokens = [
        "[NVME] Controller initialized. Admin and I/O queues online.",
        "[NVME] Verified block write to LBA 1 (Canary: 0xA55A1234).",
        "[NVME] Verified block read from LBA 1 matches canary.",
        "[INPUT] PS/2 keyboard controller online.",
        "[SHELL] Micro-GC runtime active. Surface registered with display_server.",
        "[SHELL] Executing command: 'pci' -> Discovered 3 hardware devices.",
        "[SHELL] Executing command: 'nvme' -> Block I/O benchmark passed.",
        "[DISPLAY] AVX2 compositor blitted terminal shell surface.",
        "[SUCCESS] Phase 9 fully operational. All 12 layers verified. Exiting QEMU..."
    ]

    for token in required_tokens:
        if token not in output:
            print(f"[FAIL] Missing required token in serial log: '{token}'")
            sys.exit(1)
        print(f"[PASS] Found required token: '{token}'")

    print("\n=================================================================")
    print("   ALL PHASE 9 VERIFICATION TESTS PASSED SUCCESSFULLY!          ")
    print("=================================================================")
    sys.exit(0)

if __name__ == "__main__":
    main()
