using System;
using System.Runtime.CompilerServices;
using Userland.Runtime.Gc.Memory;

namespace Userland.Runtime.Gc.Gc
{
    public static unsafe class RootScanner
    {
        public static void ScanRoots(ulong currentRsp, ulong userStackTop, ulong* calleeSavedRegisters = null)
        {
            if (!ManagedHeap.IsInitialized) return;

            ulong heapStart = (ulong)ManagedHeap.HeapStart;
            ulong heapEnd = (ulong)ManagedHeap.BumpPointer;

            // 1. Scan active user thread stack
            if (currentRsp != 0 && userStackTop > currentRsp)
            {
                ulong* ptr = (ulong*)currentRsp;
                ulong* top = (ulong*)userStackTop;
                while (ptr < top)
                {
                    ulong val = *ptr;
                    CheckAndMark(val, heapStart, heapEnd);
                    ptr++;
                }
            }

            // 2. Scan callee-saved registers (RBX, RBP, R12, R13, R14, R15)
            if (calleeSavedRegisters != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    ulong val = calleeSavedRegisters[i];
                    CheckAndMark(val, heapStart, heapEnd);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CheckAndMark(ulong val, ulong heapStart, ulong heapEnd)
        {
            if (val >= heapStart && val < heapEnd)
            {
                if ((val - heapStart) % ManagedHeap.ObjectAlignment == 0)
                {
                    GcObjectHeader* header = (GcObjectHeader*)val;
                    if (header->Magic == GcObjectHeader.HeaderMagic)
                    {
                        header->Flags |= 1; // Mark live object
                    }
                }
            }
        }
    }
}
