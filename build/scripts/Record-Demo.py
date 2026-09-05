#!/usr/bin/env python3
"""
Automated Demo GIF Recorder for SharpMetal C# Microkernel.
Captures high-resolution frame screendumps during QEMU boot via the QEMU monitor,
holds the final terminal screen for 3 seconds (30 frames), and compiles an optimized
palette-based GIF to docs/assets/demo.gif using ffmpeg.
"""

import os
import sys
import time
import glob
import shutil
import socket
import tempfile
import subprocess

def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.abspath(os.path.join(script_dir, "../.."))
    assets_dir = os.path.join(repo_root, "docs", "assets")
    os.makedirs(assets_dir, exist_ok=True)
    output_gif = os.path.join(assets_dir, "demo.gif")

    print("=================================================================")
    print("      SharpMetal Microkernel - Automated Demo GIF Recorder       ")
    print("=================================================================")

    # 1. Verify ffmpeg availability
    ffmpeg_bin = shutil.which("ffmpeg")
    if not ffmpeg_bin:
        print("[-] ERROR: ffmpeg is not installed or not in PATH.", file=sys.stderr)
        sys.exit(1)

    # 2. Setup temporary frame directory and monitor socket
    frame_dir = tempfile.mkdtemp(prefix="sharpmetal_frames_")
    sock_path = f"/tmp/qemu_record_{os.getpid()}.sock"
    if os.path.exists(sock_path):
        try:
            os.remove(sock_path)
        except OSError:
            pass

    qemu_cmd = [
        "bash", os.path.join(script_dir, "Run-Qemu.sh"),
        "-vga", "std",
        "-monitor", f"unix:{sock_path},server,nowait"
    ]

    print(f"[*] Launching QEMU with monitor at: {sock_path}")
    proc = subprocess.Popen(qemu_cmd, cwd=repo_root, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    # 3. Wait for monitor socket to become active
    sock = None
    for _ in range(50):
        if os.path.exists(sock_path):
            try:
                sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                sock.connect(sock_path)
                sock.setblocking(False)
                break
            except (socket.error, OSError):
                time.sleep(0.1)
        else:
            time.sleep(0.1)

    if not sock:
        print("[-] ERROR: Could not connect to QEMU monitor socket.", file=sys.stderr)
        proc.kill()
        shutil.rmtree(frame_dir, ignore_errors=True)
        sys.exit(1)

    print("[*] Connected to QEMU monitor. Capturing screendump frames every 150ms...")

    # 4. Polling frame capture loop
    frame_idx = 0
    try:
        while proc.poll() is None:
            frame_file = os.path.join(frame_dir, f"frame_{frame_idx:04d}.ppm")
            try:
                sock.sendall(f"screendump {frame_file}\n".encode())
                frame_idx += 1
            except (socket.error, OSError):
                break
            time.sleep(0.15)
    finally:
        try:
            sock.close()
        except Exception:
            pass
        if os.path.exists(sock_path):
            try:
                os.remove(sock_path)
            except OSError:
                pass

    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait()

    print(f"[+] QEMU finished with exit code {proc.returncode}. Captured {frame_idx} raw frames.")

    # 5. Filter valid PPM frames
    ppm_files = sorted(glob.glob(os.path.join(frame_dir, "frame_*.ppm")))
    valid_frames = [f for f in ppm_files if os.path.isfile(f) and os.path.getsize(f) > 1024]

    if not valid_frames:
        print("[-] ERROR: No valid screendump frames were captured!", file=sys.stderr)
        shutil.rmtree(frame_dir, ignore_errors=True)
        sys.exit(1)

    print(f"[+] Verified {len(valid_frames)} valid rendered frames.")

    # Re-index valid frames contiguously
    temp_staged_dir = tempfile.mkdtemp(prefix="sharpmetal_staged_")
    for idx, fpath in enumerate(valid_frames):
        dst = os.path.join(temp_staged_dir, f"frame_{idx:04d}.ppm")
        shutil.copy(fpath, dst)

    last_idx = len(valid_frames) - 1
    last_frame = os.path.join(temp_staged_dir, f"frame_{last_idx:04d}.ppm")

    # 6. Mandatory Adjustment 4: Duplicate final frame 30 times (3-second hold at 10 fps)
    print(f"[*] Holding final terminal frame for 3.0 seconds (duplicating frame 30 times)...")
    for i in range(1, 31):
        dup_dst = os.path.join(temp_staged_dir, f"frame_{last_idx + i:04d}.ppm")
        shutil.copy(last_frame, dup_dst)

    total_frames = len(valid_frames) + 30
    print(f"[+] Staged {total_frames} total frames (including 30-frame final hold).")

    # 7. Compile optimized palette GIF via ffmpeg
    print(f"[*] Encoding palette-optimized GIF to {output_gif}...")
    ffmpeg_cmd = [
        ffmpeg_bin,
        "-y",
        "-framerate", "6",
        "-i", os.path.join(temp_staged_dir, "frame_%04d.ppm"),
        "-vf", "fps=10,scale=800:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse",
        output_gif
    ]

    res = subprocess.run(ffmpeg_cmd, capture_output=True, text=True)
    if res.returncode != 0:
        print("[-] ERROR: ffmpeg encoding failed!", file=sys.stderr)
        print("STDERR:\n", res.stderr, file=sys.stderr)
        shutil.rmtree(frame_dir, ignore_errors=True)
        shutil.rmtree(temp_staged_dir, ignore_errors=True)
        sys.exit(1)

    # 8. Clean up temporary files
    shutil.rmtree(frame_dir, ignore_errors=True)
    shutil.rmtree(temp_staged_dir, ignore_errors=True)

    gif_size_kb = os.path.getsize(output_gif) / 1024.0
    print(f"[SUCCESS] Demo GIF successfully created: {output_gif} ({gif_size_kb:.1f} KB)")
    print("=================================================================")
    sys.exit(0)

if __name__ == "__main__":
    main()
