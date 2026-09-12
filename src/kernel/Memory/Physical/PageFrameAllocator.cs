using Kernel.Concurrency;

namespace Kernel.Memory.Physical
{
    public static unsafe class PageFrameAllocator
    {
        public const ulong PageSize = 4096;

        private static SpinLockWithIrqSave s_pmmLock;
        private static ulong* _bitmap;
        private static ulong _totalFrames;
        private static ulong _freeFrames;
        private static ulong _lastFoundIndex;
        private static ulong s_maxUsablePhys;

        public static ulong TotalFrames => _totalFrames;
        public static ulong FreeFrames => _freeFrames;
        public static ulong UsedFrames => _totalFrames - _freeFrames;

        public static bool IsRam(ulong phys)
        {
            // Architectural correction #2: never treat GOP framebuffer or PCI
            // MMIO mapped via MapUserMmio as managed RAM. PMM bitmap covers
            // [0, _totalFrames*4096); usable RAM is [0x100000, s_maxUsablePhys)
            // (MarkRangeFree tracks the high-water mark). Reject low memory
            // (IVT/BDA/EBDA/trampoline) and anything at/above the managed top.
            if (phys < 0x100000UL) return false;
            if (s_maxUsablePhys != 0 && phys >= s_maxUsablePhys) return false;
            if (phys >= (_totalFrames * PageSize)) return false;
            return true;
        }

        public static void Initialize(ulong* bitmapMemory, ulong totalFrames)
        {
            _bitmap = bitmapMemory;
            _totalFrames = totalFrames;
            _freeFrames = 0;
            _lastFoundIndex = 0;

            // Mark all frames as used initially
            ulong bitmapWords = (_totalFrames + 63) / 64;
            for (ulong i = 0; i < bitmapWords; i++)
            {
                _bitmap[i] = ~0UL;
            }
        }

        public static void MarkRangeFree(ulong startPhys, ulong byteLength)
        {
            ulong rflags = s_pmmLock.Acquire();
            try
            {
                ulong startFrame = startPhys / PageSize;
                ulong frameCount = byteLength / PageSize;

                for (ulong i = 0; i < frameCount; i++)
                {
                    ulong frame = startFrame + i;
                    if (frame >= _totalFrames) break;

                    ulong wordIdx = frame / 64;
                    int bitIdx = (int)(frame % 64);

                    if ((_bitmap[wordIdx] & (1UL << bitIdx)) != 0)
                    {
                        _bitmap[wordIdx] &= ~(1UL << bitIdx);
                        _freeFrames++;
                    }
                }

                ulong endPhys = startPhys + frameCount * PageSize;
                if (endPhys > s_maxUsablePhys) s_maxUsablePhys = endPhys;
            }
            finally
            {
                s_pmmLock.Release(rflags);
            }
        }

        public static void MarkRangeUsed(ulong startPhys, ulong byteLength)
        {
            ulong rflags = s_pmmLock.Acquire();
            try
            {
                ulong startFrame = startPhys / PageSize;
                ulong frameCount = (byteLength + PageSize - 1) / PageSize;

                for (ulong i = 0; i < frameCount; i++)
                {
                    ulong frame = startFrame + i;
                    if (frame >= _totalFrames) break;

                    ulong wordIdx = frame / 64;
                    int bitIdx = (int)(frame % 64);

                    if ((_bitmap[wordIdx] & (1UL << bitIdx)) == 0)
                    {
                        _bitmap[wordIdx] |= (1UL << bitIdx);
                        if (_freeFrames > 0) _freeFrames--;
                    }
                }
            }
            finally
            {
                s_pmmLock.Release(rflags);
            }
        }

        public static ulong AllocateFrame()
        {
            ulong rflags = s_pmmLock.Acquire();
            try
            {
                if (_freeFrames == 0) return 0;

                ulong bitmapWords = (_totalFrames + 63) / 64;
                for (ulong i = 0; i < bitmapWords; i++)
                {
                    ulong idx = (_lastFoundIndex + i) % bitmapWords;
                    ulong word = _bitmap[idx];
                    if (word != ~0UL)
                    {
                        // Find first zero bit
                        for (int bit = 0; bit < 64; bit++)
                        {
                            if ((word & (1UL << bit)) == 0)
                            {
                                ulong frame = (idx * 64) + (ulong)bit;
                                if (frame >= _totalFrames) return 0;

                                _bitmap[idx] |= (1UL << bit);
                                _freeFrames--;
                                _lastFoundIndex = idx;
                                return frame * PageSize;
                            }
                        }
                    }
                }

                return 0;
            }
            finally
            {
                s_pmmLock.Release(rflags);
            }
        }

        public static void FreeFrame(ulong physAddr)
        {
            ulong rflags = s_pmmLock.Acquire();
            try
            {
                ulong frame = physAddr / PageSize;
                if (frame >= _totalFrames) return;

                ulong wordIdx = frame / 64;
                int bitIdx = (int)(frame % 64);

                if ((_bitmap[wordIdx] & (1UL << bitIdx)) != 0)
                {
                    _bitmap[wordIdx] &= ~(1UL << bitIdx);
                    _freeFrames++;
                }
            }
            finally
            {
                s_pmmLock.Release(rflags);
            }
        }

        public static void FreeContiguousFrames(ulong physAddr, uint count)
        {
            for (uint i = 0; i < count; i++)
            {
                FreeFrame(physAddr + (ulong)i * PageSize);
            }
        }

        public static ulong AllocateContiguousFrames(uint count)
        {
            if (count == 0) return 0;

            ulong rflags = s_pmmLock.Acquire();
            try
            {
                if (_freeFrames < count) return 0;

                if (count == 1)
                {
                    ulong bitmapWords = (_totalFrames + 63) / 64;
                    for (ulong i = 0; i < bitmapWords; i++)
                    {
                        ulong idx = (_lastFoundIndex + i) % bitmapWords;
                        ulong word = _bitmap[idx];
                        if (word != ~0UL)
                        {
                            for (int bit = 0; bit < 64; bit++)
                            {
                                if ((word & (1UL << bit)) == 0)
                                {
                                    ulong frame = (idx * 64) + (ulong)bit;
                                    if (frame >= _totalFrames) return 0;

                                    _bitmap[idx] |= (1UL << bit);
                                    _freeFrames--;
                                    _lastFoundIndex = idx;
                                    return frame * PageSize;
                                }
                            }
                        }
                    }
                    return 0;
                }

                ulong run = 0;
                ulong runStart = 0;

                for (ulong frame = 0; frame < _totalFrames; frame++)
                {
                    ulong wordIdx = frame / 64;
                    int bitIdx = (int)(frame % 64);

                    if ((_bitmap[wordIdx] & (1UL << bitIdx)) == 0)
                    {
                        if (run == 0) runStart = frame;
                        run++;
                        if (run == count)
                        {
                            for (ulong j = 0; j < count; j++)
                            {
                                ulong f = runStart + j;
                                _bitmap[f / 64] |= (1UL << (int)(f % 64));
                                _freeFrames--;
                            }
                            return runStart * PageSize;
                        }
                    }
                    else
                    {
                        run = 0;
                    }
                }

                return 0;
            }
            finally
            {
                s_pmmLock.Release(rflags);
            }
        }
    }
}
