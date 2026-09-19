using System;

namespace Microkernel.Posix
{
    public static unsafe class PosixStackBuilder
    {
        public const ulong AT_NULL   = 0;
        public const ulong AT_PHDR   = 3;
        public const ulong AT_PHENT  = 4;
        public const ulong AT_PHNUM  = 5;
        public const ulong AT_PAGESZ = 6;
        public const ulong AT_ENTRY  = 9;
        public const ulong AT_RANDOM = 25;

        /// <summary>
        /// Builds the initial System V AMD64 ABI stack layout.
        /// Top of stack holds string payloads (argv[0], 16-byte AT_RANDOM canary).
        /// Below that is the 16-byte aligned pointer stack.
        /// Returns the final user RSP pointing to argc.
        /// </summary>
        public static ulong BuildInitialStack(
            ulong stackPageVirt,
            ulong stackTop,
            string targetPath,
            ulong entryRip,
            ulong phdrVirt,
            ulong phnum,
            ulong phent)
        {
            // 1. Strings area in the top 256 bytes of the 64KB stack
            // Store targetPath string at (stackTop - 128)
            byte* pStr = (byte*)(stackTop - 128);
            ulong pathVirt = stackTop - 128;
            int pathLen = targetPath != null ? targetPath.Length : 0;
            if (targetPath != null)
            {
                for (int i = 0; i < pathLen && i < 63; i++)
                {
                    pStr[i] = (byte)targetPath[i];
                }
            }
            pStr[pathLen < 63 ? pathLen : 63] = 0;

            // Store 16 bytes pseudo-random AT_RANDOM canary at (stackTop - 64)
            byte* pRand = (byte*)(stackTop - 64);
            ulong randVirt = stackTop - 64;
            for (int r = 0; r < 16; r++)
            {
                pRand[r] = (byte)(0x5A ^ (r * 7));
            }

            // 2. Vector stack pointer (16-byte aligned, below strings area)
            // System V AMD64 layout:
            // [rsp + 0]: argc = 1
            // [rsp + 8]: argv[0] = pathVirt
            // [rsp + 16]: argv[1] = 0 (NULL)
            // [rsp + 24]: envp[0] = 0 (NULL)
            // [rsp + 32]: AT_RANDOM (25)
            // [rsp + 40]: randVirt
            // [rsp + 48]: AT_ENTRY (9)
            // [rsp + 56]: entryRip
            // [rsp + 64]: AT_PAGESZ (6)
            // [rsp + 72]: 4096
            // [rsp + 80]: AT_PHDR (3)
            // [rsp + 88]: phdrVirt
            // [rsp + 96]: AT_PHENT (4)
            // [rsp + 104]: phent
            // [rsp + 112]: AT_PHNUM (5)
            // [rsp + 120]: phnum
            // [rsp + 128]: AT_NULL (0)
            // [rsp + 136]: 0
            // Total = 18 qwords = 144 bytes.
            // 256 + 144 = 400 bytes. Aligned user RSP: (stackTop - 400) & ~15UL.
            ulong userRsp = (stackTop - 400) & ~15UL;
            ulong* sp = (ulong*)userRsp;

            int idx = 0;
            sp[idx++] = 1;          // argc
            sp[idx++] = pathVirt;   // argv[0]
            sp[idx++] = 0;          // argv[1] = NULL
            sp[idx++] = 0;          // envp[0] = NULL

            // Auxiliary vector (auxv)
            sp[idx++] = AT_RANDOM;
            sp[idx++] = randVirt;
            sp[idx++] = AT_ENTRY;
            sp[idx++] = entryRip;
            sp[idx++] = AT_PAGESZ;
            sp[idx++] = 4096;
            sp[idx++] = AT_PHDR;
            sp[idx++] = phdrVirt;
            sp[idx++] = AT_PHENT;
            sp[idx++] = phent;
            sp[idx++] = AT_PHNUM;
            sp[idx++] = phnum;
            sp[idx++] = AT_NULL;
            sp[idx++] = 0;

            return userRsp;
        }
    }
}
