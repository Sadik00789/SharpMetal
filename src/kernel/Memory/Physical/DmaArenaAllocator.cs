namespace Kernel.Memory.Physical
{
    public static class DmaArenaAllocator
    {
        public static ulong BaseAddress { get; private set; }
        public static ulong Size { get; private set; }
        public static ulong CurrentOffset { get; private set; }

        public static void Initialize(ulong baseAddress, ulong size)
        {
            BaseAddress = baseAddress;
            Size = size;
            CurrentOffset = 0;
        }

        public static ulong Allocate(ulong bytes, ulong alignment = 4096)
        {
            if (alignment == 0) alignment = 4096;

            ulong currentPhys = BaseAddress + CurrentOffset;
            ulong alignedPhys = (currentPhys + (alignment - 1)) & ~(alignment - 1);
            ulong newOffset = (alignedPhys - BaseAddress) + bytes;

            if (newOffset > Size)
            {
                return 0; // Out of DMA arena memory
            }

            CurrentOffset = newOffset;
            return alignedPhys;
        }

        public static ulong AvailableBytes => Size > CurrentOffset ? Size - CurrentOffset : 0;
    }
}
