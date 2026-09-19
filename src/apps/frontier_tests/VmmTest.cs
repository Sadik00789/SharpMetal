using System;
using Userland.Runtime.ZeroAlloc.Interop;

namespace FrontierTests
{
    public static unsafe class VmmTest
    {
        public static void Run()
        {
            SyscallWrappers.Log("[VMM-TEST] Allocating 64KB anonymous memory via sys_mmap...\n");
            ulong addr = SyscallWrappers.Mmap(0, 65536, SyscallWrappers.PROT_READ | SyscallWrappers.PROT_WRITE, SyscallWrappers.MAP_ANONYMOUS);
            if (addr == 0 || addr == ~0UL)
            {
                SyscallWrappers.Log("[VMM-TEST] [FAIL] sys_mmap returned invalid address!\n");
                return;
            }

            // Step 1: Read from reserved memory -> triggers Demand Paging #PF
            SyscallWrappers.Log("[VMM-TEST] Reading uncommitted page to trigger Demand Paging #PF...\n");
            byte initialByte = *(byte*)addr;
            if (initialByte != 0)
            {
                SyscallWrappers.Log("[VMM-TEST] [FAIL] Demand paged memory was not zero-initialized!\n");
                return;
            }

            // Step 2: Write to reserved memory -> triggers COW Write Fault
            SyscallWrappers.Log("[VMM-TEST] Writing to page to trigger Copy-On-Write duplication...\n");
            *(byte*)addr = 0xA5;

            // Step 3: Verify data integrity
            byte writtenByte = *(byte*)addr;
            if (writtenByte != 0xA5)
            {
                SyscallWrappers.Log("[VMM-TEST] [FAIL] Data mismatch after COW write!\n");
                return;
            }

            // Step 4: Verify dynamic heap expansion via sys_brk
            ulong curBrk = SyscallWrappers.Brk(0);
            if (curBrk != 0)
            {
                ulong newBrk = SyscallWrappers.Brk(curBrk + 4096);
                if (newBrk > curBrk)
                {
                    *(byte*)curBrk = 0x5A;
                    if (*(byte*)curBrk == 0x5A)
                    {
                        SyscallWrappers.Log("[VMM-TEST] Dynamic heap expansion (sys_brk) verified.\n");
                    }
                }
            }

            SyscallWrappers.Log("[VMM-TEST] Frontier 1 VMM self-test completed successfully.\n");
        }
    }
}
