using System;

namespace Userland.Runtime.ZeroAlloc.Memory
{
    public unsafe struct NativeArena
    {
        public byte* Buffer;
        public nuint Capacity;
        public nuint Offset;

        public void Initialize(byte* buffer, nuint capacity)
        {
            Buffer = buffer;
            Capacity = capacity;
            Offset = 0;
        }

        public void* Allocate(nuint size, nuint alignment = 16)
        {
            if (Buffer == null || size == 0) return null;

            nuint currentPtr = (nuint)(Buffer + Offset);
            nuint alignedPtr = (currentPtr + (alignment - 1)) & ~(alignment - 1);
            nuint padding = alignedPtr - currentPtr;

            if (Offset + padding + size > Capacity)
            {
                return null; // Out of memory in arena
            }

            Offset += padding + size;
            return (void*)alignedPtr;
        }

        public void* AllocateChunk(nuint chunkSize)
        {
            return Allocate(chunkSize, 16);
        }

        public void Reset()
        {
            Offset = 0;
        }

        public nuint AllocatedBytes => Offset;
        public nuint RemainingBytes => Capacity >= Offset ? Capacity - Offset : 0;
    }
}
