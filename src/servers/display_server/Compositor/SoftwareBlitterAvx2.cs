using System;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Boot;
using Userland.Runtime.ZeroAlloc.Interop;

namespace DisplayServer.Compositor
{
    public static unsafe class SoftwareBlitterAvx2
    {
        [DllImport("*")]
        public static extern void Avx2Blit(void* dst, void* src, ulong byteCount);

        [DllImport("*")]
        public static extern void Avx2Fill(void* dst, uint color, ulong pixelCount);

        public const ulong FramebufferVirt = 0x30000000UL;
        public static uint Width = 0;
        public static uint Height = 0;
        public static uint Pitch = 0;
        public static ulong FbSize = 0;

        public static void Initialize()
        {
            KernelBootInfo bootInfo = default;
            SyscallWrappers.GetBootInfo(&bootInfo);

            Width = bootInfo.GopWidth;
            Height = bootInfo.GopHeight;
            Pitch = bootInfo.GopPixelsPerScanLine > 0 ? bootInfo.GopPixelsPerScanLine : bootInfo.GopWidth;
            FbSize = bootInfo.GopFbSize;

            if (bootInfo.GopPhysBase != 0 && bootInfo.GopFbSize > 0)
            {
                SyscallWrappers.MapMmio(bootInfo.GopPhysBase, FramebufferVirt, bootInfo.GopFbSize, writeCombining: true);
                SyscallWrappers.Log("[DISPLAY] GOP Framebuffer mapped via SysMapMmio (Phys != 0).\n");
            }
            else
            {
                SyscallWrappers.Log("[DISPLAY] Headless mode: Allocating dummy fallback framebuffer.\n");
                Width = 1024;
                Height = 768;
                Pitch = 1024;
                FbSize = 1024 * 768 * 4;
                SyscallWrappers.AllocDma(FbSize, FramebufferVirt);
            }
        }

        public static void ClearFramebuffer(uint color = 0xFF1E1E2E)
        {
            if (FramebufferVirt != 0 && Pitch > 0 && Height > 0)
            {
                uint* fb = (uint*)FramebufferVirt;
                ulong totalPixels = FbSize / 4UL;
                ulong pitchPixels = (ulong)Pitch * (ulong)Height;
                if (totalPixels == 0 || totalPixels < pitchPixels)
                {
                    totalPixels = pitchPixels;
                }
                for (ulong i = 0; i < totalPixels; i++)
                {
                    fb[i] = color;
                }
            }

            SyscallWrappers.Log("[DISPLAY] AVX2 software compositor initialized. Framebuffer cleared.\n");
        }

        public static void BlitClientSurface(uint* src, uint srcWidth, uint srcHeight, uint x, uint y, uint w, uint h)
        {
            if (FramebufferVirt != 0 && Pitch > 0 && src != null)
            {
                uint* fb = (uint*)FramebufferVirt;
                for (uint row = y; row < y + h && row < Height && row < srcHeight; row++)
                {
                    uint* dstRow = fb + (row * Pitch) + x;
                    uint* srcRow = src + (row * srcWidth) + x;
                    for (uint col = 0; col < w && (col + x) < srcWidth; col++)
                    {
                        dstRow[col] = srcRow[col];
                    }
                }
            }
        }
    }
}
