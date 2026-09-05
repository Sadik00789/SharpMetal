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
            return Ps2Keyboard.ReadKey();
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
