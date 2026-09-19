using System;
using System.Runtime.InteropServices;
using Kernel.Concurrency;

namespace Kernel.Memory.Physical
{
    public static unsafe class PhysicalFrameRefcount
    {
        public const int MaxFrames = 1048576; // Up to 4 GiB physical memory

        [StructLayout(LayoutKind.Sequential, Size = MaxFrames * 2)]
        private struct RefcountStorage
        {
        }

        private static RefcountStorage s_storage;
        private static ushort* s_refcounts;
        private static SpinLockWithIrqSave s_lock;

        public static void Initialize()
        {
            fixed (RefcountStorage* ptr = &s_storage)
            {
                s_refcounts = (ushort*)ptr;
                for (int i = 0; i < MaxFrames; i++)
                {
                    s_refcounts[i] = 0;
                }
            }
        }

        public static ushort Get(ulong phys)
        {
            ulong frame = phys / PageFrameAllocator.PageSize;
            if (frame >= (ulong)MaxFrames || s_refcounts == null) return 1;

            ulong rflags = s_lock.Acquire();
            try
            {
                return s_refcounts[frame];
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static void Set(ulong phys, ushort count)
        {
            ulong frame = phys / PageFrameAllocator.PageSize;
            if (frame >= (ulong)MaxFrames || s_refcounts == null) return;

            ulong rflags = s_lock.Acquire();
            try
            {
                s_refcounts[frame] = count;
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static ushort Increment(ulong phys)
        {
            ulong frame = phys / PageFrameAllocator.PageSize;
            if (frame >= (ulong)MaxFrames || s_refcounts == null) return 1;

            ulong rflags = s_lock.Acquire();
            try
            {
                if (s_refcounts[frame] < 0xFFFF)
                {
                    s_refcounts[frame]++;
                }
                return s_refcounts[frame];
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static ushort Decrement(ulong phys)
        {
            ulong frame = phys / PageFrameAllocator.PageSize;
            if (frame >= (ulong)MaxFrames || s_refcounts == null) return 0;

            ulong rflags = s_lock.Acquire();
            try
            {
                if (s_refcounts[frame] > 0)
                {
                    s_refcounts[frame]--;
                }
                return s_refcounts[frame];
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }
    }
}
