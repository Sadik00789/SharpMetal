using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Boot;
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

        public static volatile int s_spinSink;

        public static void DelayPaced(int spinCount = 3000000)
        {
            for (int i = 0; i < spinCount; i++)
            {
                s_spinSink = i;
            }
            SyscallWrappers.Yield();
        }

        public static void RenderAndCommit(ref DisplayServiceClient displayClient, ulong surfaceId)
        {
            Grid.Render(ref ShellSurface, Color32.TerminalFg, Color32.TerminalBg);
            displayClient.CommitSurface((uint)surfaceId, 0, 0, 640, 400);
            SyscallWrappers.Yield();
        }

        private static void NavigateHistory(
            ref DisplayServiceClient displayClient,
            ulong surfaceId,
            byte* cmdBuffer,
            ref int cmdLen,
            ref int historyIndex,
            int direction)
        {
            int total = TerminalHistory.Count;
            if (total == 0) return;

            int newIndex = historyIndex + direction;
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= total) newIndex = total - 1;

            byte* histEntry = stackalloc byte[128];
            int entryLen = 0;
            if (!TerminalHistory.TryGetEntry(newIndex, histEntry, out entryLen)) return;

            historyIndex = newIndex;

            // Erase current command from screen grid
            while (cmdLen > 0)
            {
                Grid.WriteChar('\b');
                cmdLen--;
            }

            // Copy entry into cmdBuffer and display
            for (int i = 0; i < entryLen && i < 120; i++)
            {
                cmdBuffer[i] = histEntry[i];
                Grid.WriteChar((char)histEntry[i]);
            }
            cmdLen = entryLen < 120 ? entryLen : 120;
            cmdBuffer[cmdLen] = 0;

            RenderAndCommit(ref displayClient, surfaceId);
        }

        public static void RunInteractiveLoop(ref DisplayServiceClient displayClient, ulong surfaceId)
        {
            var inputClient = new InputServiceClient(endpointCptr: 10);
            byte* cmdBuffer = stackalloc byte[128];
            int cmdLen = 0;
            cmdBuffer[0] = 0;

            int historyIndex = -1;

            while (true)
            {
                uint key = inputClient.ReadKey();
                if (key != 0)
                {
                    if (key == '\n' || key == '\r')
                    {
                        Grid.WriteChar('\n');
                        cmdBuffer[cmdLen] = 0;

                        if (cmdLen > 0)
                        {
                            TerminalHistory.Add(cmdBuffer, cmdLen);
                            historyIndex = -1;

                            if (ShellEngine.MatchCommand(cmdBuffer, cmdLen, "exit") ||
                                ShellEngine.MatchCommand(cmdBuffer, cmdLen, "poweroff") ||
                                ShellEngine.MatchCommand(cmdBuffer, cmdLen, "shutdown"))
                            {
                                Grid.WriteString("[SHELL] Powering off system...\n");
                                RenderAndCommit(ref displayClient, surfaceId);
                                SyscallWrappers.Exit(0);
                            }
                            else if (ShellEngine.MatchCommand(cmdBuffer, cmdLen, "reboot") ||
                                     ShellEngine.MatchCommand(cmdBuffer, cmdLen, "reset"))
                            {
                                Grid.WriteString("[SHELL] Rebooting system...\n");
                                RenderAndCommit(ref displayClient, surfaceId);
                                SyscallWrappers.Exit(1);
                            }
                            else
                            {
                                ShellEngine.ExecuteCommand(cmdBuffer, cmdLen, ref Grid);
                            }
                        }

                        cmdLen = 0;
                        cmdBuffer[0] = 0;
                        Grid.WriteString("kernel:> ");
                        RenderAndCommit(ref displayClient, surfaceId);
                    }
                    else if (key == '\b')
                    {
                        if (cmdLen > 0)
                        {
                            cmdLen--;
                            cmdBuffer[cmdLen] = 0;
                            Grid.WriteChar('\b');
                            RenderAndCommit(ref displayClient, surfaceId);
                        }
                    }
                    else if (key == 0x1B) // Escape or ANSI escape prefix
                    {
                        uint next1 = inputClient.ReadKey();
                        if (next1 == '[')
                        {
                            uint next2 = inputClient.ReadKey();
                            if (next2 == 'A') // Up Arrow: Previous command
                            {
                                NavigateHistory(ref displayClient, surfaceId, cmdBuffer, ref cmdLen, ref historyIndex, 1);
                            }
                            else if (next2 == 'B') // Down Arrow: Next command
                            {
                                NavigateHistory(ref displayClient, surfaceId, cmdBuffer, ref cmdLen, ref historyIndex, -1);
                            }
                        }
                    }
                    else if (key >= 32 && key <= 126) // Printable ASCII
                    {
                        if (cmdLen < 120)
                        {
                            cmdBuffer[cmdLen++] = (byte)key;
                            cmdBuffer[cmdLen] = 0;
                            Grid.WriteChar((char)key);
                            RenderAndCommit(ref displayClient, surfaceId);
                        }
                    }
                }
                else
                {
                    SyscallWrappers.Yield();
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "ShellMain")]
        public static void Main()
        {
            KernelBootInfo bootInfo = default;
            SyscallWrappers.GetBootInfo(&bootInfo);

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
            TerminalHistory.Add("cat /HELLO.TXT");
            TerminalHistory.Add("pci");
            TerminalHistory.Add("nvme");
            TerminalHistory.Add("net");
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
            RenderAndCommit(ref displayClient, surfaceId);
            DelayPaced(3000000);

            // 8. Command 1: 'cat /HELLO.TXT'
            Grid.WriteString("kernel:> cat /HELLO.TXT\n");
            ShellEngine.ExecuteCommand("cat /HELLO.TXT", ref Grid);
            RenderAndCommit(ref displayClient, surfaceId);
            DelayPaced(3000000);

            // 9. Command 2: 'pci'
            Grid.WriteString("kernel:> pci\n");
            ShellEngine.ExecuteCommand("pci", ref Grid);
            RenderAndCommit(ref displayClient, surfaceId);
            DelayPaced(3000000);

            // 10. Command 3: 'nvme'
            Grid.WriteString("kernel:> nvme\n");
            ShellEngine.ExecuteCommand("nvme", ref Grid);
            RenderAndCommit(ref displayClient, surfaceId);
            DelayPaced(3000000);

            // 11. Command 4: 'net'
            Grid.WriteString("kernel:> net\n");
            ShellEngine.ExecuteCommand("net", ref Grid);
            RenderAndCommit(ref displayClient, surfaceId);
            DelayPaced(3000000);

            if (bootInfo.IsHypervisor != 0)
            {
                // In automated QEMU test harness: execute scripted 'exit' command to finish CI verification
                Grid.WriteString("kernel:> exit\n");
                RenderAndCommit(ref displayClient, surfaceId);
                DelayPaced(3000000);

                ShellEngine.ExecuteCommand("exit", ref Grid);
            }
            else
            {
                // On bare-metal physical hardware: transition directly into interactive shell
                Grid.WriteString("\n[SHELL] Interactive mode online. Type 'help' for commands.\n");
                Grid.WriteString("kernel:> ");
                RenderAndCommit(ref displayClient, surfaceId);

                RunInteractiveLoop(ref displayClient, surfaceId);
            }
        }
    }
}
