namespace Microkernel.Abstractions.Syscalls
{
    public static class SyscallNumbers
    {
        // Phase 4 primitives
        public const ulong SysYield   = 0x01;
        public const ulong SysGetTid  = 0x02;
        public const ulong SysLog          = 0x03;
        public const ulong SysExit         = 0x04;
        public const ulong SysCreateThread = 0x05;
        public const ulong SysMapMmio      = 0x06;
        public const ulong SysGetBootInfo  = 0x07;
        public const ulong SysCreateProcess= 0x08;
        public const ulong SysGetPhysicalAddress = 0x09;
        public const ulong SysAllocDma      = 0x0A;

        // Phase 5 IPC primitives
        public const ulong SysSend    = 0x10;
        public const ulong SysRecv    = 0x11;
        public const ulong SysCall    = 0x12;
        public const ulong SysReply   = 0x13;
        public const ulong SysNotify  = 0x14;
        public const ulong SysRecvAny = 0x15;
    }
}
