namespace Kernel.Memory.Physical
{
    public struct MemoryRegion
    {
        public ulong BaseAddress;
        public ulong Size;
        public uint Type;

        public MemoryRegion(ulong baseAddress, ulong size, uint type)
        {
            BaseAddress = baseAddress;
            Size = size;
            Type = type;
        }

        public ulong EndAddress => BaseAddress + Size;
    }
}
