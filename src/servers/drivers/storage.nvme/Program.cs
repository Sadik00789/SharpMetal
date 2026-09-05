using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace StorageNvme
{
    public struct BlockStorageServiceImpl : IBlockStorageService
    {
        public ulong ReadBlock(ulong lba, ulong shmCptr)
        {
            return NvmeDriver.ReadBlock(lba, shmCptr);
        }

        public ulong WriteBlock(ulong lba, ulong shmCptr)
        {
            return NvmeDriver.WriteBlock(lba, shmCptr);
        }
    }

    public static unsafe class Program
    {
        public static BlockStorageServiceImpl s_serviceImpl;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "StorageNvmeMain")]
        public static void Main()
        {
            // Initialize NVMe hardware, allocate DMA rings, and verify canary
            NvmeDriver.Initialize();

            // Run RPC dispatcher on Slot 9
            s_serviceImpl = new BlockStorageServiceImpl();
            bool running = true;
            BlockStorageServiceDispatcher.Run(ref s_serviceImpl, endpointCptr: 9, ref running);

            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
