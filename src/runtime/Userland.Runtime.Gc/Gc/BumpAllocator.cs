using System;
using Userland.Runtime.Gc.Memory;

namespace Userland.Runtime.Gc.Gc
{
    public static unsafe class BumpAllocator
    {
        public static void* Allocate(uint payloadSize)
        {
            if (!ManagedHeap.IsInitialized) return null;

            uint headerSize = (uint)sizeof(GcObjectHeader);
            uint totalSize = (headerSize + payloadSize + (ManagedHeap.ObjectAlignment - 1)) & ~(ManagedHeap.ObjectAlignment - 1);

            if (ManagedHeap.BumpPointer + totalSize > ManagedHeap.HeapEnd)
            {
                return null; // Slab exhausted
            }

            GcObjectHeader* header = (GcObjectHeader*)ManagedHeap.BumpPointer;
            header->TotalSize = totalSize;
            header->Flags = 0; // Unmarked
            header->Magic = GcObjectHeader.HeaderMagic;

            ManagedHeap.BumpPointer += totalSize;
            ManagedHeap.TotalObjectsAllocated++;

            return (void*)header;
        }

        public static void Reset()
        {
            ManagedHeap.BumpPointer = ManagedHeap.HeapStart;
            ManagedHeap.TotalObjectsAllocated = 0;
        }
    }
}
