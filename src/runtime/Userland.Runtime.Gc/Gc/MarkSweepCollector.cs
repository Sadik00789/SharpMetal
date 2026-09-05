using System;
using Userland.Runtime.Gc.Memory;

namespace Userland.Runtime.Gc.Gc
{
    public static unsafe class MarkSweepCollector
    {
        public static uint LastReclaimedCount = 0;
        public static uint LastLiveCount = 0;

        public static uint Collect(ulong currentRsp, ulong userStackTop, ulong* calleeSavedRegisters = null)
        {
            if (!ManagedHeap.IsInitialized) return 0;

            // 1. Mark Phase: scan active user stack and callee-saved registers
            RootScanner.ScanRoots(currentRsp, userStackTop, calleeSavedRegisters);

            // 2. Sweep Phase: traverse all objects in managed heap
            uint reclaimed = 0;
            uint live = 0;

            byte* curr = ManagedHeap.HeapStart;
            byte* end = ManagedHeap.BumpPointer;

            while (curr < end)
            {
                GcObjectHeader* header = (GcObjectHeader*)curr;
                if (header->Magic != GcObjectHeader.HeaderMagic && header->Magic != 0xDEADBEEFUL)
                {
                    break; // Corrupted or uninitialized region
                }

                if ((header->Flags & 1) == 0)
                {
                    // Unmarked: unreachable garbage
                    reclaimed++;
                    header->Magic = 0xDEADBEEFUL; // Mark dead
                }
                else
                {
                    // Marked: live object
                    live++;
                    header->Flags &= ~1U; // Unmark for subsequent collection cycles
                }

                if (header->TotalSize == 0) break;
                curr += header->TotalSize;
            }

            LastReclaimedCount = reclaimed;
            LastLiveCount = live;

            return reclaimed;
        }
    }
}
