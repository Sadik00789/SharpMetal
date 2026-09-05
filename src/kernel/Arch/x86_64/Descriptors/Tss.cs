using System;
using System.Runtime.InteropServices;

namespace Kernel.Arch.x86_64.Descriptors
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct TaskStateSegment
    {
        public uint Reserved0;
        public ulong Rsp0;
        public ulong Rsp1;
        public ulong Rsp2;
        public ulong Reserved1;
        public ulong Ist1;
        public ulong Ist2;
        public ulong Ist3;
        public ulong Ist4;
        public ulong Ist5;
        public ulong Ist6;
        public ulong Ist7;
        public ulong Reserved2;
        public ushort Reserved3;
        public ushort IoMapBase;

        public const ushort Size = 104;

        public static TaskStateSegment* Instance;

        public void Initialize(ulong rsp0)
        {
            Reserved0 = 0;
            Rsp0 = rsp0;
            Rsp1 = 0;
            Rsp2 = 0;
            Reserved1 = 0;
            Ist1 = 0;
            Ist2 = 0;
            Ist3 = 0;
            Ist4 = 0;
            Ist5 = 0;
            Ist6 = 0;
            Ist7 = 0;
            Reserved2 = 0;
            Reserved3 = 0;
            IoMapBase = Size; // Points to end of TSS, disabling user port I/O
        }

        public static void SetRsp0(ulong rsp0)
        {
            ulong alignedRsp0 = rsp0 & ~15UL;
            if (Instance != null)
            {
                Instance->Rsp0 = alignedRsp0;
            }
            Hardware.Cpu.SetSyscallKernelRsp(alignedRsp0);
        }
    }
}
