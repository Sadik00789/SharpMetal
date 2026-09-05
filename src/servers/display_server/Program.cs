using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DisplayServer.Compositor;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace DisplayServer
{
    public unsafe struct DisplayServiceImpl : IDisplayService
    {
        public static ulong s_clientSurfaceVirt = 0x38000000UL;
        public static uint s_surfaceWidth = 640;
        public static uint s_surfaceHeight = 400;
        public static bool s_surfaceMapped = false;

        public ulong RegisterSurface(uint width, uint height, ulong shmCptr)
        {
            s_surfaceWidth = width;
            s_surfaceHeight = height;

            // Constraint 1: Cross-Process Surface Physical Mapping
            // shmCptr is the 64-bit physical address passed by the client.
            // display_server maps this physical region into its own PML4 via MapMmio.
            if (shmCptr != 0)
            {
                ulong sizeBytes = (ulong)width * (ulong)height * 4UL;
                SyscallWrappers.MapMmio(shmCptr, s_clientSurfaceVirt, sizeBytes, writeCombining: false);
                s_surfaceMapped = true;
            }
            return 1; // Surface ID 1
        }

        public uint CommitSurface(uint surfaceId, uint x, uint y, uint w, uint h)
        {
            if (s_surfaceMapped)
            {
                uint cx = x, cy = y, cw = w, ch = h;
                if (DirtyRegionTracker.Clip(ref cx, ref cy, ref cw, ref ch, SoftwareBlitterAvx2.Width, SoftwareBlitterAvx2.Height))
                {
                    SoftwareBlitterAvx2.BlitClientSurface((uint*)s_clientSurfaceVirt, s_surfaceWidth, s_surfaceHeight, cx, cy, cw, ch);
                }
            }
            SyscallWrappers.Log("[DISPLAY] AVX2 compositor blitted alpha-blended surface.\n");
            return 0;
        }
    }

    public static unsafe class Program
    {
        public static DisplayServiceImpl s_serviceImpl;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "DisplayServerMain")]
        public static void Main()
        {
            // Map GOP framebuffer and render test pattern with AVX2
            SoftwareBlitterAvx2.Initialize();
            SoftwareBlitterAvx2.RenderTestPattern();

            // Run RPC dispatcher on Slot 7
            s_serviceImpl = new DisplayServiceImpl();
            bool running = true;
            DisplayServiceDispatcher.Run(ref s_serviceImpl, endpointCptr: 7, ref running);

            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
