using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetVirtio
{
    public struct NetworkServiceImpl : INetworkService
    {
        public uint GetMacAddress(ulong outMacBufferPhys)
        {
            return VirtioNetDriver.GetMacAddress(outMacBufferPhys);
        }

        public uint SendPacket(ulong packetPhys, uint length)
        {
            return VirtioNetDriver.SendPacket(packetPhys, length);
        }

        public uint ReceivePacket(ulong packetPhys, uint maxLength)
        {
            return VirtioNetDriver.ReceivePacket(packetPhys, maxLength);
        }
    }

    public static unsafe class Program
    {
        public static NetworkServiceImpl s_serviceImpl;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "VirtioNetMain")]
        public static void Main()
        {
            // Initialize VirtIO Modern Network device and configure virtqueues
            VirtioNetDriver.Initialize();

            // Run RPC dispatcher on Slot 12
            s_serviceImpl = new NetworkServiceImpl();
            bool running = true;
            NetworkServiceDispatcher.Run(ref s_serviceImpl, endpointCptr: 12, ref running);

            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
