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

            // 5. Initialize 32-slot command history ring buffer
            TerminalHistory.Initialize();
            TerminalHistory.Add("pci");
            TerminalHistory.Add("nvme");
            TerminalHistory.Add("cat /HELLO.TXT");
            TerminalHistory.Add("exit");
            SyscallWrappers.Log("[SHELL] History ring buffer initialized (32 slots).\n");

            // 6. Test VFS File.ReadAllText
            string helloText = System.IO.File.ReadAllText("/HELLO.TXT");
            SyscallWrappers.Log("[VFS] File.ReadAllText('/HELLO.TXT') -> \"");
            SyscallWrappers.Log(helloText);
            SyscallWrappers.Log("\"\n");

            // 7. Initialize terminal text grid and banner
            Grid.Initialize();
            Grid.WriteString("=================================================================\n");
            Grid.WriteString("   SharpMetal Microkernel - Interactive Graphic Shell (Phase 10)\n");
            Grid.WriteString("=================================================================\n");

            // 8. Execute automated integration commands: 'pci'
            Grid.WriteString("kernel:> pci\n");
            ShellEngine.ExecuteCommand("pci", ref Grid);

            // 9. Execute automated integration commands: 'nvme'
            Grid.WriteString("kernel:> nvme\n");
            ShellEngine.ExecuteCommand("nvme", ref Grid);

            // 10. Execute automated integration commands: 'cat /HELLO.TXT'
            Grid.WriteString("kernel:> cat /HELLO.TXT\n");
            ShellEngine.ExecuteCommand("cat /HELLO.TXT", ref Grid);

            // 11. Execute automated integration commands: 'net'
            Grid.WriteString("kernel:> net\n");
            ShellEngine.ExecuteCommand("net", ref Grid);

            // 12. Render virtual console to surface with alpha-blended font
            Grid.WriteString("kernel:> exit\n");
            Grid.Render(ref ShellSurface, Color32.TerminalFg, Color32.TerminalBg);

            // Commit surface to display_server -> AVX2 Blit -> Token
            displayClient.CommitSurface((uint)surfaceId, 0, 0, 640, 400);

            // Small yield so display_server processes the commit RPC
            SyscallWrappers.Yield();

            // 13. Execute 'exit' command -> SysExit(0) -> QEMU exit 33
            ShellEngine.ExecuteCommand("exit", ref Grid);

            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
