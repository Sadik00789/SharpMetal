namespace Kernel.Memory.Virtual
{
    public static class Hhdm
    {
        public const ulong Base = 0xFFFF_8000_0000_0000UL;
        public const ulong KernelHighBase = 0xFFFF_FFFF_8000_0000UL;

        public static ulong PhysicalToVirtual(ulong phys)
        {
            return Base + phys;
        }

        public static ulong VirtualToPhysical(ulong virt)
        {
            if (virt >= Base && virt < KernelHighBase)
            {
                return virt - Base;
            }
            if (virt >= KernelHighBase)
            {
                return virt - KernelHighBase;
            }
            return virt; // Fallback identity
        }
    }
}
