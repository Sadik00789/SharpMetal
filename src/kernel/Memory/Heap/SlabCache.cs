using System;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;

namespace Kernel.Memory.Heap
{
    public unsafe struct SlabCache
    {
        public ulong BlockSize;
        private void* _freeListHead;
        private ulong _totalBlocks;
        private ulong _allocatedBlocks;

        public ulong TotalBlocks => _totalBlocks;
        public ulong AllocatedBlocks => _allocatedBlocks;
        public ulong FreeBlocks => _totalBlocks - _allocatedBlocks;

        public void Initialize(ulong blockSize)
        {
            BlockSize = blockSize;
            _freeListHead = null;
            _totalBlocks = 0;
            _allocatedBlocks = 0;
        }

        public void* Allocate()
        {
            if (_freeListHead == null)
            {
                if (!Grow())
                {
                    return null; // Out of physical frames
                }
            }

            void* block = _freeListHead;
            _freeListHead = *(void**)_freeListHead;
            _allocatedBlocks++;
            return block;
        }

        public void Free(void* ptr)
        {
            if (ptr == null) return;

            *(void**)ptr = _freeListHead;
            _freeListHead = ptr;
            if (_allocatedBlocks > 0)
            {
                _allocatedBlocks--;
            }
        }

        private bool Grow()
        {
            ulong framePhys = PageFrameAllocator.AllocateFrame();
            if (framePhys == 0)
            {
                return false;
            }

            ulong frameVirt = Hhdm.PhysicalToVirtual(framePhys);
            uint blockCount = (uint)(PageFrameAllocator.PageSize / BlockSize);

            // Carve 4 KiB frame into contiguous size-class blocks with intrusive pointers
            for (uint i = 0; i < blockCount; i++)
            {
                byte* block = (byte*)frameVirt + (i * BlockSize);
                byte* nextBlock = (i + 1 < blockCount) ? (block + BlockSize) : (byte*)_freeListHead;
                *(void**)block = nextBlock;
            }

            _freeListHead = (void*)frameVirt;
            _totalBlocks += blockCount;
            return true;
        }
    }
}
