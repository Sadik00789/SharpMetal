using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Drawing;
using Microkernel.Abstractions.Services;
using Userland.Runtime.Gc.Memory;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Shell
{
    public static unsafe class Program
    {
        [DllImport("*")]
        public static extern byte* GetFontGlyphs();

        public static TerminalGrid Grid;
        public static Surface ShellSurface;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "ShellMain")]
        public static void Main()
        {
            // 1. Initialize Micro-GC runtime (Layer 8)
            ulong gcSlabPhys = SyscallWrappers.AllocDma(65536, 0x39000000UL);
            byte* gcSlab = (byte*)0x39000000UL;
            ManagedHeap.Initialize(gcSlab, 65536);

            // 2. Initialize embedded PSF2 8x16 bitmap font
            byte* fontData = GetFontGlyphs();
            BitmapFont.Initialize(fontData);

            // 3. Constraint 1: Cross-Process Surface Physical Mapping
            // Allocate 640x400 ARGB32 buffer (1024 KiB) and obtain its 64-bit physical address
            ulong surfaceVirt = 0x38000000UL;
            ulong surfacePhys = SyscallWrappers.AllocDma(1048576, surfaceVirt);
            uint* pixels = (uint*)surfaceVirt;
            ShellSurface = new Surface(pixels, 640, 400);
            ShellSurface.Clear(Color32.TerminalBg);

            // 4. Register surface with display_server on endpoint 7 using physical address
            var displayClient = new DisplayServiceClient(endpointCptr: 7);
            ulong surfaceId = displayClient.RegisterSurface(640, 400, surfacePhys);

            // Serial Token 5
            SyscallWrappers.Log("[SHELL] Micro-GC runtime active. Surface registered with display_server.\n");

            // 5. Initialize terminal text grid and banner
            Grid.Initialize();
            Grid.WriteString("=================================================================\n");
            Grid.WriteString("   Baremetal C# Microkernel - Interactive Graphic Shell (Phase 9)\n");
            Grid.WriteString("=================================================================\n");

            // 6. Execute automated integration commands: 'pci'
            Grid.WriteString("kernel:> pci\n");
            ShellEngine.ExecuteCommand("pci", ref Grid);

            // 7. Execute automated integration commands: 'nvme'
            Grid.WriteString("kernel:> nvme\n");
            ShellEngine.ExecuteCommand("nvme", ref Grid);

            // 8. Render virtual console to surface and commit to display_server via AVX2
            Grid.WriteString("kernel:> exit\n");
            Grid.Render(ref ShellSurface, Color32.TerminalFg, Color32.TerminalBg);

            // Commit surface to display_server -> AVX2 Blit -> Token 8
            displayClient.CommitSurface((uint)surfaceId, 0, 0, 640, 400);

            // Small yield so display_server processes the commit RPC
            SyscallWrappers.Yield();

            // 9. Execute 'exit' command -> SysExit(0) -> Token 9 -> QEMU exit 33
            ShellEngine.ExecuteCommand("exit", ref Grid);

            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
