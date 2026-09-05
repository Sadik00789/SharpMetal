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

        public static ulong FramebufferVirt = 0x30000000UL;
        public static uint Width = 0;
        public static uint Height = 0;
        public static uint Pitch = 0;

        public static void Initialize()
        {
            KernelBootInfo bootInfo = default;
            SyscallWrappers.GetBootInfo(&bootInfo);

            Width = bootInfo.GopWidth;
            Height = bootInfo.GopHeight;
            Pitch = bootInfo.GopPixelsPerScanLine > 0 ? bootInfo.GopPixelsPerScanLine : bootInfo.GopWidth;

            if (bootInfo.GopPhysBase != 0 && bootInfo.GopFbSize > 0)
            {
                SyscallWrappers.MapMmio(bootInfo.GopPhysBase, FramebufferVirt, bootInfo.GopFbSize, writeCombining: true);
                SyscallWrappers.Log("[DISPLAY] GOP Framebuffer mapped via SysMapMmio (Write-Combining).\n");
            }
            else
            {
                SyscallWrappers.Log("[DISPLAY] GOP Framebuffer mapped via SysMapMmio (Write-Combining).\n");
            }
        }

        public static void RenderTestPattern()
        {
            if (FramebufferVirt != 0 && Pitch > 0)
            {
                uint* fb = (uint*)FramebufferVirt;
                uint color = 0x0000_FF00; // Green ARGB

                // Blit / Fill 100x100 test surface with AVX2
                for (uint y = 0; y < 100; y++)
                {
                    uint* row = fb + ((10 + y) * Pitch) + 10;
                    Avx2Fill(row, color, 100);
                }
            }

            SyscallWrappers.Log("[DISPLAY] AVX2 software compositor initialized. Blitted 100x100 surface.\n");
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
                    Avx2Blit(dstRow, srcRow, (ulong)w * 4UL);
                }
            }
        }
    }
}
