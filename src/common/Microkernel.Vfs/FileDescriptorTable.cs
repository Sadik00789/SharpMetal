using System;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Microkernel.Vfs
{
    public static unsafe class FileDescriptorTable
    {
        public const int MaxDescriptors = 32;
        private static uint* s_endpointCptr = null;
        private static uint* s_serverHandle = null;
        private static ulong* s_currentOffset = null;
        private static byte* s_inUse = null;

        public static void EnsureInitialized()
        {
            if (s_inUse == null)
            {
                SyscallWrappers.AllocDma(4096, 0x3D000000UL);
                s_inUse = (byte*)0x3D000000UL;
                s_endpointCptr = (uint*)(0x3D000000UL + 64);
                s_serverHandle = (uint*)(0x3D000000UL + 256);
                s_currentOffset = (ulong*)(0x3D000000UL + 512);
                for (int i = 0; i < MaxDescriptors; i++)
                {
                    s_inUse[i] = 0;
                }
            }
        }

        public static int Allocate(uint endpointCptr, uint serverHandle)
        {
            EnsureInitialized();
            for (int i = 0; i < MaxDescriptors; i++)
            {
                if (s_inUse[i] == 0)
                {
                    s_inUse[i] = 1;
                    s_endpointCptr[i] = endpointCptr;
                    s_serverHandle[i] = serverHandle;
                    s_currentOffset[i] = 0;
                    return i;
                }
            }
            return -1;
        }

        public static bool Get(int fd, out uint endpointCptr, out uint serverHandle, out ulong currentOffset)
        {
            EnsureInitialized();
            if (fd >= 0 && fd < MaxDescriptors && s_inUse[fd] == 1)
            {
                endpointCptr = s_endpointCptr[fd];
                serverHandle = s_serverHandle[fd];
                currentOffset = s_currentOffset[fd];
                return true;
            }
            endpointCptr = 0;
            serverHandle = 0;
            currentOffset = 0;
            return false;
        }

        public static void Free(int fd)
        {
            EnsureInitialized();
            if (fd >= 0 && fd < MaxDescriptors)
            {
                s_inUse[fd] = 0;
            }
        }
    }
}
