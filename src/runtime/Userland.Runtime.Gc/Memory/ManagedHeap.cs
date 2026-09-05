using System;
using System.Runtime.InteropServices;

namespace Userland.Runtime.Gc.Memory
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GcObjectHeader
    {
        public const ulong HeaderMagic = 0x47434F424A454354UL; // "GCOBJECT"

        public uint TotalSize; // Total size in bytes including header
        public uint Flags;     // Bit 0: Marked bit
        public ulong Magic;    // Verification magic
    }

    public static unsafe class ManagedHeap
    {
        public const uint ObjectAlignment = 16;
        public const nuint SlabSize = 65536; // 64 KiB per slab

        public static byte* HeapStart = null;
        public static byte* HeapEnd = null;
        public static byte* BumpPointer = null;
        public static nuint TotalCapacity = 0;
        public static uint TotalObjectsAllocated = 0;

        public static void Initialize(byte* slabMemory, nuint capacity)
        {
            HeapStart = slabMemory;
            HeapEnd = slabMemory + capacity;
            BumpPointer = slabMemory;
            TotalCapacity = capacity;
            TotalObjectsAllocated = 0;
        }

        public static bool IsInitialized => HeapStart != null && TotalCapacity > 0;
    }
}
