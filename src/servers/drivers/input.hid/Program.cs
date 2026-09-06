using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace InputHid
{
    public struct InputServiceImpl : IInputService
    {
        public uint ReadKey()
        {
            // 1. Check PS/2 Keyboard (both built-in keyboard and USB keyboard via SMM legacy emulation)
            uint key = Ps2Keyboard.ReadKey();
            if (key != 0) return key;

            // 2. Check COM1 Serial Port (0x3F8) if character is waiting (e.g. QEMU / serial console)
            if (HasSerialInput())
            {
                byte c = Ps2Keyboard.PortIn8(0x3F8);
                if (c == '\r') return '\n';
                if (c != 0) return c;
            }

            return 0;
        }

        private static bool HasSerialInput()
        {
            // COM1 Line Status Register (0x3FD): Bit 0 = Data Ready
            return (Ps2Keyboard.PortIn8(0x3FD) & 0x01) != 0;
        }
    }

    public static unsafe class Program
    {
        public static InputServiceImpl s_serviceImpl;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "InputHidMain")]
        public static void Main()
        {
            // Initialize PS/2 Keyboard controller and scancode tables
            Ps2Keyboard.Initialize();

            // Run RPC dispatcher on Slot 10
            s_serviceImpl = new InputServiceImpl();
            bool running = true;
            InputServiceDispatcher.Run(ref s_serviceImpl, endpointCptr: 10, ref running);

            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
