using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace FsFat32
{
    public struct FilesystemServiceImpl : IFilesystemService
    {
        public ulong Open(ulong pathShmCptr, uint flags)
        {
            return Fat32Driver.Open(pathShmCptr, flags);
        }

        public ulong Read(uint fileHandle, ulong outBufferPhys, ulong offset, ulong length)
        {
            return Fat32Driver.Read(fileHandle, outBufferPhys, offset, length);
        }

        public ulong GetFileSize(uint fileHandle)
        {
            return Fat32Driver.GetFileSize(fileHandle);
        }

        public uint Close(uint fileHandle)
        {
            return Fat32Driver.Close(fileHandle);
        }
    }

    public static unsafe class Program
    {
        public static FilesystemServiceImpl s_serviceImpl;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "Fat32Main")]
        public static void Main()
        {
            // Initialize FAT32 filesystem from NVMe block storage
            Fat32Driver.Initialize();

            // Run RPC dispatcher on Slot 11
            s_serviceImpl = new FilesystemServiceImpl();
            bool running = true;
            FilesystemServiceDispatcher.Run(ref s_serviceImpl, endpointCptr: 11, ref running);

            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
