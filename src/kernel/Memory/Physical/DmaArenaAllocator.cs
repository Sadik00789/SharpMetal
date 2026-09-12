namespace Kernel.Memory.Physical
{
    /// <summary>
    /// Bump allocator for sub-4GiB bus-mastering DMA buffers.
    /// Hardened: strict [MinAddress, MaxAddress) confinement so a compromised
    /// caller can never allocate or expose physical memory outside the DMA zone.
    /// </summary>
    public static class DmaArenaAllocator
    {
        public static ulong BaseAddress { get; private set; }
        public static ulong Size { get; private set; }
        public static ulong CurrentOffset { get; private set; }

        /// <summary>Inclusive lower bound of the DMA zone (== BaseAddress).</summary>
        public static ulong MinAddress => BaseAddress;

        /// <summary>
        /// Exclusive upper bound of the DMA zone (== BaseAddress + Size).
        /// Computed with overflow saturation: on wrap returns BaseAddress.
        /// </summary>
        public static ulong MaxAddress
        {
            get
            {
                ulong end = BaseAddress + Size;
                // Detect wrap-around: if end < BaseAddress the addition overflowed.
                if (end < BaseAddress) return BaseAddress;
                return end;
            }
        }

        public static void Initialize(ulong baseAddress, ulong size)
        {
            // Reject degenerate zones outright; leave allocator in a fail-closed state.
            if (baseAddress == 0 || size == 0)
            {
                BaseAddress = 0;
                Size = 0;
                CurrentOffset = 0;
                return;
            }

            // Reject zones whose [base, base+size) wraps the 64-bit address space.
            if (size > 0xFFFFFFFFFFFFFFFFUL - baseAddress)
            {
                BaseAddress = 0;
                Size = 0;
                CurrentOffset = 0;
                return;
            }

            BaseAddress = baseAddress;
            Size = size;
            CurrentOffset = 0;
        }

        /// <summary>
        /// Returns true iff [phys, phys+length) lies wholly inside the DMA zone.
        /// Rejects zero-length, wrapping, and out-of-zone ranges.
        /// </summary>
        public static bool ValidateRange(ulong phys, ulong length)
        {
            if (length == 0) return false;
            if (Size == 0) return false;
            ulong max = MaxAddress;
            if (phys < BaseAddress) return false;
            if (phys >= max) return false;
            // Overflow check: phys + length must not wrap.
            if (length > 0xFFFFFFFFFFFFFFFFUL - phys) return false;
            ulong end = phys + length;
            if (end > max) return false;
            return true;
        }

        /// <summary>Returns true iff phys is a valid DMA-zone byte address.</summary>
        public static bool IsDmaAddress(ulong phys)
        {
            if (Size == 0) return false;
            return phys >= BaseAddress && phys < MaxAddress;
        }

        private static bool IsPowerOfTwo(ulong v)
        {
            return v != 0 && (v & (v - 1)) == 0;
        }

        public static ulong Allocate(ulong bytes, ulong alignment = 4096)
        {
            // Fail-closed on uninitialised / degenerate zones.
            if (Size == 0 || BaseAddress == 0) return 0;
            // Reject zero-byte requests: they must not yield a usable DMA address.
            if (bytes == 0) return 0;
            if (alignment == 0) alignment = 4096;
            // Alignment must be a power of two; non-conforming requests are rejected
            // rather than silently rounded (prevents caller confusion / over-mapping).
            if (!IsPowerOfTwo(alignment)) return 0;
            // Request larger than the entire arena can never succeed.
            if (bytes > Size) return 0;

            // Guard: CurrentOffset must never exceed Size (corruption check).
            if (CurrentOffset > Size) return 0;

            ulong currentPhys = BaseAddress + CurrentOffset;
            // currentPhys cannot wrap: CurrentOffset <= Size and Base+Size is validated.
            ulong mask = alignment - 1;
            // (currentPhys + mask) overflow check.
            if (mask > 0xFFFFFFFFFFFFFFFFUL - currentPhys) return 0;
            ulong alignedPhys = (currentPhys + mask) & ~mask;

            // Strict zone confinement: aligned start must be inside the zone.
            if (alignedPhys < MinAddress) return 0;
            if (alignedPhys >= MaxAddress) return 0;
            // End must not wrap and must stay within the zone.
            if (bytes > 0xFFFFFFFFFFFFFFFFUL - alignedPhys) return 0;
            ulong endPhys = alignedPhys + bytes;
            if (endPhys > MaxAddress) return 0;

            ulong newOffset = endPhys - BaseAddress;
            if (newOffset > Size) return 0;

            CurrentOffset = newOffset;
            return alignedPhys;
        }

        public static ulong AvailableBytes => Size > CurrentOffset ? Size - CurrentOffset : 0;
    }
}
