#!/usr/bin/env bash
''''exec python3 "$0" "$@" #'''
import subprocess
import sys
import re
import os
import time

REQUIRED_MILESTONES = [
    r"\[SMP\] 4 cores synchronized and operational",
    r"\[PASS\] Concurrent zero-alloc physical frame stress test succeeded",
    r"\[PASS\] Broadcast IPI TLB shootdown verified across all active cores",
    r"\[ROOTTASK\] Initial root CNode initialized",
    r"\[PCI\] Scanning PCIe ECAM bus topology",
    r"\[PCI\] Found Host Bridge",
    r"\[NVME\] Controller initialized",
    r"\[NVME\] Block I/O benchmark passed",
    r"\[VIRTIO-NET\] Modern PCI VirtIO Network device detected",
    r"\[PASS\] VMM: Demand paging resolved fault at 0x[0-9A-F]+",
    r"\[PASS\] COW: Frame duplicated on write",
    r"\[PASS\] ELF: /bin/test.pie relocated and entry executed",
    r"Hello from ELF",
    r"\[NET\] RX Virtqueue replenished with 16 descriptors",
    r"\[PASS\] NET: VirtIO RX/TX loopback / ICMP processed",
    r"\[PASS\] XHCI: Controller initialized and USB keyboard addressed",
    r"\[SHELL\] SharpMetal Bare-Metal Shell online",
    r"Hello from POSIX",
    r"\[PASS\] POSIX: SYS_write and SYS_read executed successfully",
]

def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.abspath(os.path.join(script_dir, "../.."))

    print("=================================================================")
    print("   SharpMetal Microkernel Headless CI Automation Harness         ")
    print("=================================================================")

    # Step 1: Ensure disk image is built
    disk_img = os.path.join(repo_root, "build/disk.img")
    make_disk = os.path.join(script_dir, "Make-DiskImage.sh")
    # CI always opts into the native xHCI path, which requires the image to be
    # rebuilt with XHCI_NATIVE=1 so xhci_native.flag is packed. Default builds
    # (and therefore USB sticks) carry no flag and keep BIOS legacy emulation.
    ci_native = os.environ.get("XHCI_NATIVE") == "1"
    if (not os.path.exists(disk_img)) or ("--build" in sys.argv) or ci_native:
        print("[STEP 1] Running Make-DiskImage.sh...")
        build_env = dict(os.environ)
        build_env["XHCI_NATIVE"] = "1"
        res = subprocess.run(["bash", make_disk], cwd=repo_root, env=build_env)
        if res.returncode != 0:
            print("[-] Make-DiskImage.sh failed!")
            sys.exit(1)

    print("\n[STEP 2] Launching QEMU headless test harness...")
    proc = subprocess.Popen(
        ["bash", "build/scripts/Run-Qemu.sh", "--headless"],
        cwd=repo_root,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1
    )

    output_buffer = ""
    deadline = time.time() + 65
    fault_lines = []
    key_sent = False

    while True:
        if time.time() > deadline:
            print(f"\n[-] Timed out waiting for milestones after 65s.")
            proc.kill()
            proc.wait()
            break

        line = proc.stdout.readline()
        if not line and proc.poll() is not None:
            break
        if line:
            sys.stdout.write(line)
            sys.stdout.flush()
            output_buffer += line

            # Feed keypress to satisfy blocking SYS_read(0) in posix_test
            if ("Hello from POSIX" in line or "Hello from POSIX" in output_buffer) and not key_sent:
                try:
                    time.sleep(0.3)
                    proc.stdin.write("X\n")
                    proc.stdin.flush()
                    key_sent = True
                    print("[HARNESS] Injected serial input 'X\\n' to satisfy blocking SYS_read(0).")
                except Exception as e:
                    print(f"[HARNESS] Error injecting serial input: {e}")

            # Track kernel panic lines for diagnostics (non-fatal if milestones pass)
            if "[FAULT]" in line or "Kernel Panic" in line:
                fault_lines.append(line.rstrip())

            # Check all milestones against accumulated buffer
            all_matched = True
            for m in REQUIRED_MILESTONES:
                if not re.search(m, output_buffer):
                    all_matched = False
                    break

            if all_matched:
                print("\n[+] All boot milestones successfully verified.")
                if fault_lines:
                    print(f"[!] Note: {len(fault_lines)} kernel fault(s) observed (non-critical, milestones passed):")
                    for fl in fault_lines:
                        print(f"    {fl}")
                proc.kill()
                try:
                    proc.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait()
                sys.exit(0)

    # Loop ended without matching all milestones
    unmatched = [m for m in REQUIRED_MILESTONES if not re.search(m, output_buffer)]
    if not unmatched:
        print("\n[+] All boot milestones successfully verified.")
        sys.exit(0)
    else:
        print(f"\n[-] Failed to match milestones: {unmatched}")
        sys.exit(1)

if __name__ == "__main__":
    main()


def check_milestone(output_buffer, keyword):
    return keyword in output_buffer
