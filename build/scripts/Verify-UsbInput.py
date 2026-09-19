#!/usr/bin/env python3
"""End-to-end USB HID input verification for the SharpMetal xHCI driver.

Boots the image with qemu-xhci plus a single usb-kbd, waits for the xHCI
milestone, injects keystrokes through the QEMU monitor `sendkey` command, and
asserts that input.hid accepted an injected key. This proves the full chain:
qemu-xhci -> bus.xhci Interrupt IN report -> IInputService.InjectKey -> input.hid.
"""
import os
import socket
import subprocess
import sys
import time

MONITOR_SOCK = "/tmp/sm-qemu-monitor.sock"
XHCI_PASS = "[PASS] XHCI: Controller initialized and USB keyboard addressed"
# emitted by input.hid when the InjectKey RPC enqueues a key.
INJECT_TOKEN = "[INPUT] USB HID inject accepted."
# emitted when the shell's ReadKey actually drains an injected key.
DRAIN_TOKEN = "[INPUT] USB HID injected key accepted."


def connect_monitor():
    for _ in range(40):
        try:
            s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            s.connect(MONITOR_SOCK)
            s.settimeout(5)
            return s
        except OSError:
            time.sleep(0.25)
    return None


def wait_for_udp_port_free(port=8080, timeout=20.0):
    """Run-Qemu.sh forwards UDP 8080, so wait out a lingering QEMU."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.bind(("", port))
            s.close()
            return True
        except OSError:
            s.close()
            time.sleep(0.5)
    return False


def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.abspath(os.path.join(script_dir, "../.."))

    if os.path.exists(MONITOR_SOCK):
        os.unlink(MONITOR_SOCK)

    if not wait_for_udp_port_free():
        print("[-] UDP port 8080 stayed busy; another QEMU is still running.")
        return 1

    print("=================================================================")
    print("   SharpMetal xHCI USB HID End-to-End Input Verification         ")
    print("=================================================================")

    proc = subprocess.Popen(
        ["bash", "build/scripts/Run-Qemu.sh", "--headless",
         "-monitor", "unix:{},server,nowait".format(MONITOR_SOCK)],
        cwd=repo_root,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1,
    )

    saw_xhci = False
    saw_inject = False
    deadline = time.time() + 60

    while time.time() < deadline:
        line = proc.stdout.readline()
        if not line:
            if proc.poll() is not None:
                break
            continue

        sys.stdout.write(line)
        sys.stdout.flush()

        if not saw_xhci and XHCI_PASS in line:
            saw_xhci = True
            # Send immediately: the kernel powers the machine off once its
            # scripted demo finishes, so the interactive window is short.
            monitor = connect_monitor()
            if monitor is None:
                print("[-] Could not connect to the QEMU monitor socket")
            else:
                for key in ("a", "b", "c"):
                    try:
                        monitor.sendall("sendkey {}\n".format(key).encode())
                    except OSError:
                        break
                try:
                    monitor.close()
                except OSError:
                    pass

        if INJECT_TOKEN in line or DRAIN_TOKEN in line:
            saw_inject = True
            break

    proc.kill()
    try:
        proc.wait(timeout=5)
    except Exception:
        pass

    if saw_xhci and saw_inject:
        print("\n[+] USB HID input path verified end to end.")
        return 0

    if not saw_xhci:
        print("\n[-] xHCI milestone was not observed.")
    if not saw_inject:
        print("\n[-] Injected-key token was not observed.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
