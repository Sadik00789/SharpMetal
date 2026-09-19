using System;
using Microkernel.Vfs;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Roottask
{
    public static unsafe class ProcessLauncher
    {
        private static void PrintHex(ulong val)
        {
            byte* hexDigits = stackalloc byte[16];
            for (int i = 0; i < 10; i++) hexDigits[i] = (byte)('0' + i);
            for (int i = 0; i < 6; i++) hexDigits[10 + i] = (byte)('A' + i);

            byte* buf = stackalloc byte[17];
            int idx = 0;
            for (int shift = 60; shift >= 0; shift -= 4)
            {
                byte nibble = (byte)((val >> shift) & 0xF);
                if (idx > 0 || nibble > 0 || shift == 0)
                {
                    buf[idx++] = hexDigits[nibble];
                }
            }
            buf[idx] = 0;
            SyscallWrappers.Log(buf);
        }

        public static ulong LaunchElf(string vfsPath)
        {
            SyscallWrappers.Log("[ROOTTASK] Spawning ELF process: ");
            if (vfsPath != null)
            {
                fixed (char* p = vfsPath)
                {
                    for (int i = 0; i < vfsPath.Length; i++)
                    {
                        byte b = (byte)p[i];
                        byte* single = stackalloc byte[2];
                        single[0] = b;
                        single[1] = 0;
                        SyscallWrappers.Log(single);
                    }
                }
            }
            SyscallWrappers.Log("\n");

            // 1. Open file via VFS IPC with retries until filesystem server is ready
            ulong fileHandle = 0;
            for (int retry = 0; retry < 50; retry++)
            {
                fileHandle = VfsClient.Open(vfsPath, 0);
                if (fileHandle != 0 && fileHandle < 100)
                {
                    break;
                }
                SyscallWrappers.Yield();
            }

            SyscallWrappers.Log("[ROOTTASK] fileHandle = 0x");
            PrintHex(fileHandle);
            SyscallWrappers.Log("\n");

            if (fileHandle == 0 || fileHandle >= 100)
            {
                SyscallWrappers.Log("[ROOTTASK] ERROR: Failed to open ELF binary via VFS IPC!\n");
                return 0;
            }

            // 2. Allocate DMA buffer to read ELF header (4096 bytes)
            ulong dmaVirt = 0x3E000000UL;
            ulong dmaPhys = SyscallWrappers.AllocDma(4096, dmaVirt);
            if (dmaPhys == 0)
            {
                SyscallWrappers.Log("[ROOTTASK] ERROR: Failed to allocate DMA buffer for ELF header!\n");
                VfsClient.Close((uint)fileHandle);
                return 0;
            }

            // Read the first 4KB of the ELF binary
            ulong bytesRead = VfsClient.Read((uint)fileHandle, dmaPhys, 0, 4096);
            SyscallWrappers.Log("[ROOTTASK] bytesRead = 0x");
            PrintHex(bytesRead);
            SyscallWrappers.Log("\n");

            if (bytesRead < 64 || bytesRead >= 0xFFFFFFFFFFFFFF00UL)
            {
                SyscallWrappers.Log("[ROOTTASK] ERROR: Read less than ELF header size from VFS!\n");
                VfsClient.Close((uint)fileHandle);
                return 0;
            }

            // 3. Delegate to Kernel via SysSpawnElf
            ulong tid = SyscallWrappers.SpawnElf(dmaPhys, bytesRead, (uint)fileHandle, priority: 2);
            if (tid == 0)
            {
                SyscallWrappers.Log("[ROOTTASK] ERROR: SysSpawnElf failed in kernel!\n");
                VfsClient.Close((uint)fileHandle);
                return 0;
            }

            return tid;
        }
    }
}
