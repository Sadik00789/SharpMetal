#!/usr/bin/env python3
import subprocess
import sys
import re
import os
import time

REQUIRED_MILESTONES = [
    r"\[ROOTTASK\] Initial root CNode initialized",
    r"\[PCI\] Scanning PCIe ECAM bus topology",
    r"\[PCI\] Found Host Bridge",
    r"\[NVME\] Controller initialized",
    r"\[NVME\] Block I/O benchmark passed",
    r"\[VIRTIO-NET\] Modern PCI VirtIO Network device detected",
    r"\[SHELL\] SharpMetal Bare-Metal Shell online",
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
    if not os.path.exists(disk_img) or "--build" in sys.argv:
        print("[STEP 1] Running Make-DiskImage.sh...")
        res = subprocess.run(["bash", make_disk], cwd=repo_root)
        if res.returncode != 0:
            print("[-] Make-DiskImage.sh failed!")
            sys.exit(1)

    print("\n[STEP 2] Launching QEMU headless test harness...")
    proc = subprocess.Popen(
        ["bash", "build/scripts/Run-Qemu.sh", "--headless"],
        cwd=repo_root,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1
    )

    matched = 0
    deadline = time.time() + 45

    while True:
        if time.time() > deadline:
            print(f"\n[-] Timed out waiting for milestones after 45s.")
            proc.kill()
            break

        line = proc.stdout.readline()
        if not line and proc.poll() is not None:
            break
        if line:
            sys.stdout.write(line)
            sys.stdout.flush()
            if matched < len(REQUIRED_MILESTONES):
                if re.search(REQUIRED_MILESTONES[matched], line):
                    matched += 1

    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait()

    if matched == len(REQUIRED_MILESTONES):
        print("\n[+] All boot milestones successfully verified.")
        sys.exit(0)
    else:
        print(f"\n[-] Failed to match milestone {matched}: {REQUIRED_MILESTONES[matched]}")
        sys.exit(1)

if __name__ == "__main__":
    main()
