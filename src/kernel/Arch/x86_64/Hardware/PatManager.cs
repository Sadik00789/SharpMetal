namespace Kernel.Arch.x86_64.Hardware
{
    public static class PatManager
    {
        public const uint Ia32PatMsr = 0x277;
        public const byte MemoryTypeWc = 0x01; // Write-Combining

        public static ulong Initialize()
        {
            ulong pat = Cpu.ReadMsr(Ia32PatMsr);

            // Program entry PA4 (bits 32..39) as Write-Combining (0x01)
            pat &= ~(0xFFUL << 32);
            pat |= ((ulong)MemoryTypeWc << 32);

            Cpu.WriteMsr(Ia32PatMsr, pat);
            return pat;
        }
    }
}
