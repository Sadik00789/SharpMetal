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
            // Never treat <1MiB (IVT/BDA/EBDA/trampoline), DMA arena buffers,
            // kernel/UEFI regions, GOP framebuffer, or PCI MMIO as PMM-managed RAM.
            if (phys < 0x100000UL) return false;
            if (DmaArenaAllocator.Size != 0 && phys >= DmaArenaAllocator.MinAddress && phys < DmaArenaAllocator.MaxAddress) return false;
            if (s_maxUsablePhys != 0 && phys >= s_maxUsablePhys) return false;
            if (phys >= (_totalFrames * PageSize)) return false;

            int count = Kernel.Boot.KernelHigh.UsableMemoryMap.RegionCount;
            if (count <= 0) return false;
            if (count > Kernel.Boot.KernelHigh.BootMemoryMap.MaxRegions) count = Kernel.Boot.KernelHigh.BootMemoryMap.MaxRegions;
            for (int i = 0; i < count; i++)
            {
                ulong start = Kernel.Boot.KernelHigh.UsableMemoryMap.RegionStarts[i];
                ulong pages = Kernel.Boot.KernelHigh.UsableMemoryMap.RegionPageCounts[i];
                if (pages == 0 || pages > 0xFFFFFFFFFFFFFFFFUL / PageSize) continue;
                ulong size = pages * PageSize;
                if (size > 0xFFFFFFFFFFFFFFFFUL - start) continue;
                if (phys >= start && phys < start + size) return true;
            }
            return false;
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
            if (byteLength == 0 || _bitmap == null) return;
            ulong rflags = s_pmmLock.Acquire();
            try
            {
                ulong rawEnd = byteLength <= 0xFFFFFFFFFFFFFFFFUL - startPhys ? startPhys + byteLength : 0xFFFFFFFFFFFFFFFFUL;
                // Preserve low memory < 1 MiB (IVT, BDA, EBDA, AP trampoline at 0x8000)
                ulong clampedStart = startPhys < 0x100000UL ? 0x100000UL : startPhys;
                clampedStart = (clampedStart + PageSize - 1) & ~(PageSize - 1);
                ulong clampedEnd = rawEnd & ~(PageSize - 1);
                if (clampedEnd <= clampedStart) return;

                ulong startFrame = clampedStart / PageSize;
                ulong frameCount = (clampedEnd - clampedStart) / PageSize;
                ulong markedEndPhys = clampedStart;

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
                    markedEndPhys = (frame + 1) * PageSize;
                }

                if (markedEndPhys > s_maxUsablePhys) s_maxUsablePhys = markedEndPhys;
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
            if (physAddr < 0x100000UL) return;
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
