using Kernel.Concurrency;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;

namespace Kernel.Memory.Heap
{
    public static unsafe class SlabAllocator
    {
        private static SpinLockWithIrqSave s_slabLock;
        private static SlabCache s_cache32;
        private static SlabCache s_cache64;
        private static SlabCache s_cache128;
        private static SlabCache s_cache256;
        private static SlabCache s_cache512;
        private static SlabCache s_cache1024;
        private static SlabCache s_cache2048;
        private static SlabCache s_cache4096;

        public static void Initialize()
        {
            // Size classes are powers of 2. For classes >= 64, carving 4096-byte frames
            // guarantees that all blocks are at least 64-byte aligned (s_cache2048 for ThreadControlBlock
            // provides 2048-byte alignment, satisfying XSAVE/XRSTOR 64-byte alignment requirements).
            s_cache32.Initialize(32);
            s_cache64.Initialize(64);
            s_cache128.Initialize(128);
            s_cache256.Initialize(256);
            s_cache512.Initialize(512);
            s_cache1024.Initialize(1024);
            s_cache2048.Initialize(2048);
            s_cache4096.Initialize(4096);
        }

        public static void* KmAlloc(ulong size)
        {
            if (size == 0) return null;

            ulong rflags = s_slabLock.Acquire();
            try
            {
                if (size <= 32)   return s_cache32.Allocate();
                if (size <= 64)   return s_cache64.Allocate();
                if (size <= 128)  return s_cache128.Allocate();
                if (size <= 256)  return s_cache256.Allocate();
                if (size <= 512)  return s_cache512.Allocate();
                if (size <= 1024) return s_cache1024.Allocate();
                if (size <= 2048) return s_cache2048.Allocate();
                if (size <= 4096) return s_cache4096.Allocate();

                // Large allocations (> 4096 bytes) bypass slab and allocate contiguous frames
                uint pages = (uint)((size + 4095) / 4096);
                ulong phys = PageFrameAllocator.AllocateContiguousFrames(pages);
                if (phys == 0) return null;

                return (void*)Hhdm.PhysicalToVirtual(phys);
            }
            finally
            {
                s_slabLock.Release(rflags);
            }
        }

        public static void KmFree(void* ptr, ulong size)
        {
            if (ptr == null || size == 0) return;

            ulong rflags = s_slabLock.Acquire();
            try
            {
                if (size <= 32)   { s_cache32.Free(ptr); return; }
                if (size <= 64)   { s_cache64.Free(ptr); return; }
                if (size <= 128)  { s_cache128.Free(ptr); return; }
                if (size <= 256)  { s_cache256.Free(ptr); return; }
                if (size <= 512)  { s_cache512.Free(ptr); return; }
                if (size <= 1024) { s_cache1024.Free(ptr); return; }
                if (size <= 2048) { s_cache2048.Free(ptr); return; }
                if (size <= 4096) { s_cache4096.Free(ptr); return; }

                uint pages = (uint)((size + 4095) / 4096);
                ulong phys = Hhdm.VirtualToPhysical((ulong)ptr);
                for (uint i = 0; i < pages; i++)
                {
                    PageFrameAllocator.FreeFrame(phys + ((ulong)i * 4096));
                }
            }
            finally
            {
                s_slabLock.Release(rflags);
            }
        }

        public static T* Alloc<T>() where T : unmanaged
        {
            return (T*)KmAlloc((ulong)sizeof(T));
        }

        public static void Free<T>(T* ptr) where T : unmanaged
        {
            KmFree(ptr, (ulong)sizeof(T));
        }
    }
}
