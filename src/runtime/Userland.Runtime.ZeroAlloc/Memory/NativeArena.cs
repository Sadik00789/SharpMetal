using System;

namespace Userland.Runtime.ZeroAlloc.Memory
{
    public unsafe struct ArenaChunk
    {
        public ArenaChunk* Next;
        public ulong Size;
        public ulong Allocated;
        public byte* Data;
    }

    public static unsafe class NativeArena
    {
        private const ulong DefaultChunkSize = 64 * 1024; // 64 KiB
        private static ulong s_nextDmaVirt = 0x0000_7000_0000_0000UL;
        
        public static ArenaChunk* FirstChunk = null;
        public static ArenaChunk* CurrentChunk = null;

        public static void Initialize(byte* initialBuffer, ulong initialCapacity)
        {
            ArenaChunk* chunk = (ArenaChunk*)initialBuffer;
            chunk->Next = null;
            chunk->Size = initialCapacity - (ulong)sizeof(ArenaChunk);
            chunk->Allocated = 0;
            chunk->Data = initialBuffer + sizeof(ArenaChunk);

            FirstChunk = chunk;
            CurrentChunk = chunk;
        }

        public static void* Allocate(nuint size, nuint alignment = 16)
        {
            if (CurrentChunk == null) return null;

            ulong alignedAlloc = (CurrentChunk->Allocated + (alignment - 1)) & ~(alignment - 1);
            if (alignedAlloc + size <= CurrentChunk->Size)
            {
                void* ptr = CurrentChunk->Data + alignedAlloc;
                CurrentChunk->Allocated = alignedAlloc + size;
                return ptr;
            }

            // Chunk exhausted: expand arena via dynamic DMA mapping
            ulong required = size + alignment + (ulong)sizeof(ArenaChunk);
            ulong chunkSize = required > DefaultChunkSize ? (required + 0xFFFUL) & ~0xFFFUL : DefaultChunkSize;

            ulong targetVirt = s_nextDmaVirt;
            s_nextDmaVirt += chunkSize;

            ulong phys = Interop.SyscallWrappers.AllocDma(chunkSize, targetVirt);
            if (phys == 0) return null;

            ArenaChunk* newChunk = (ArenaChunk*)targetVirt;
            newChunk->Next = null;
            newChunk->Size = chunkSize - (ulong)sizeof(ArenaChunk);
            newChunk->Allocated = 0;
            newChunk->Data = (byte*)targetVirt + sizeof(ArenaChunk);

            CurrentChunk->Next = newChunk;
            CurrentChunk = newChunk;

            alignedAlloc = (CurrentChunk->Allocated + (alignment - 1)) & ~(alignment - 1);
            CurrentChunk->Allocated = alignedAlloc + size;
            return CurrentChunk->Data + alignedAlloc;
        }

        public static void* AllocateChunk(nuint chunkSize)
        {
            return Allocate(chunkSize, 16);
        }

        public static void Reset()
        {
            ArenaChunk* curr = FirstChunk;
            while (curr != null)
            {
                curr->Allocated = 0;
                curr = curr->Next;
            }
            CurrentChunk = FirstChunk;
        }
    }
}
