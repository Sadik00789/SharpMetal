using System.Runtime.InteropServices;

namespace Kernel.Arch.x86_64.Hardware
{
    public static unsafe class Cpu
    {
        public const uint Ia32Efer  = 0xC0000080;
        public const uint Ia32Star  = 0xC0000081;
        public const uint Ia32Lstar = 0xC0000082;
        public const uint Ia32Fmask = 0xC0000084;

        [DllImport("*")]
        public static extern ulong ReadCr0();

        [DllImport("*")]
        public static extern void WriteCr0(ulong value);

        [DllImport("*")]
        public static extern ulong ReadCr3();

        [DllImport("*")]
        public static extern ulong ReadCr2();

        [DllImport("*")]
        public static extern void WriteCr3(ulong value);

        [DllImport("*")]
        public static extern ulong ReadCr4();

        [DllImport("*")]
        public static extern void WriteCr4(ulong value);

        [DllImport("*")]
        public static extern ulong ReadMsr(uint msr);

        [DllImport("*")]
        public static extern void WriteMsr(uint msr, ulong value);

        [DllImport("*")]
        public static extern void DisableInterrupts();

        [DllImport("*")]
        public static extern void EnableInterrupts();

        [DllImport("*")]
        public static extern void Invlpg(ulong virtAddr);

        [DllImport("*")]
        public static extern ulong ReadRflags();

        [DllImport("*")]
        public static extern void RestoreRflags(ulong rflags);

        [DllImport("*")]
        public static extern ulong GetRsp();

        [DllImport("*")]
        public static extern ulong GetRip();

        [DllImport("*")]
        public static extern void SwitchToHigherHalf(ulong pml4Phys, ulong highRsp, ulong entryPointHigh);

        [DllImport("*")]
        public static extern void LoadGdt(void* gdtPointer);

        [DllImport("*")]
        public static extern void ReloadSegments(ushort codeSeg, ushort dataSeg);

        [DllImport("*")]
        public static extern void LoadTss(ushort tssSelector);

        [DllImport("*")]
        public static extern void LoadIdt(void* idtPointer);

        [DllImport("*")]
        public static extern ulong* GetIsrThunkTable();

        [DllImport("*")]
        public static extern void ContextSwitch(ulong* oldRspOut, ulong newRsp);

        [DllImport("*")]
        public static extern ulong DoSyscall(ulong num, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6);

        [DllImport("*")]
        public static extern ulong GetSyscallEntry();

        [DllImport("*")]
        public static extern ulong GetThreadStartTrampoline();

        [DllImport("*")]
        public static extern void EnterUserMode(ulong entryRip, ulong userRsp, ulong pml4Phys);

        [DllImport("*")]
        public static extern void SetSyscallKernelRsp(ulong rsp0);

        [DllImport("*")]
        public static extern ulong GetUserThreadTrampoline();

        [DllImport("*")]
        public static extern void XSetBv(uint ecx, ulong value);

        public static void EnableAvx()
        {
            // Set CR4.OSFXSR (bit 9), CR4.OSXMMEXCPT (bit 10), and CR4.OSXSAVE (bit 18)
            ulong cr4 = ReadCr4();
            cr4 |= (1UL << 9);
            cr4 |= (1UL << 10);
            cr4 |= (1UL << 18);
            WriteCr4(cr4);

            // Clear CR0.EM (bit 2) and set CR0.MP (bit 1)
            ulong cr0 = ReadCr0();
            cr0 &= ~(1UL << 2);
            cr0 |= (1UL << 1);
            WriteCr0(cr0);

            // Configure XCR0 to enable x87 (bit 0), SSE (bit 1), and AVX (bit 2)
            XSetBv(0, 0x07UL);
        }

        [DllImport("*")]
        public static extern uint CpuIdEcx(uint leaf);

        [DllImport("*")]
        public static extern void Halt();

        [DllImport("*")]
        public static extern void TripleFaultReset();

        public static bool IsHypervisor()
        {
            uint ecx = CpuIdEcx(1);
            if ((ecx & (1u << 31)) != 0)
            {
                return true;
            }

            if (Boot.KernelHigh.RsdpPhysBase >= 0x1000UL && Boot.KernelHigh.RsdpPhysBase < (16UL * 1024 * 1024 * 1024))
            {
                byte* rsdp = (byte*)Memory.Virtual.Hhdm.PhysicalToVirtual(Boot.KernelHigh.RsdpPhysBase);
                if (rsdp[9] == 'B' && rsdp[10] == 'O' && rsdp[11] == 'C' && rsdp[12] == 'H' && rsdp[13] == 'S')
                {
                    return true;
                }
                if (rsdp[9] == 'Q' && rsdp[10] == 'E' && rsdp[11] == 'M' && rsdp[12] == 'U')
                {
                    return true;
                }
            }

            return false;
        }
    }
}
