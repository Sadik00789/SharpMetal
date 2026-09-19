namespace Microkernel.Abstractions.Syscalls
{
    public static class SyscallNumbers
    {
        // Phase 4 primitives
        public const ulong SysYield   = 0x01;
        public const ulong Yield      = SysYield;
        public const ulong SysGetTid  = 0x02;
        public const ulong SysLog          = 0x03;
        public const ulong SysExit         = 0x04;
        public const ulong SysCreateThread = 0x05;
        public const ulong SysMapMmio      = 0x06;
        public const ulong SysGetBootInfo  = 0x07;
        public const ulong SysCreateProcess= 0x08;
        public const ulong SysGetPhysicalAddress = 0x09;
        public const ulong SysAllocDma      = 0x0A;
        public const ulong SysSpawn         = 0x0B;
        public const ulong Spawn            = SysSpawn;
        public const ulong SysSpawnElf      = 0x0C;

        // Phase 5 IPC primitives
        public const ulong SysSend    = 0x10;
        public const ulong SysRecv    = 0x11;
        public const ulong SysCall    = 0x12;
        public const ulong SysReply   = 0x13;
        public const ulong SysNotify  = 0x14;
        public const ulong SysRecvAny = 0x15;

        // Dynamic Memory primitives
        public const ulong SysMmap    = 0x20;
        public const ulong SysBrk     = 0x21;

        // POSIX / I/O primitives
        public const ulong SysRead    = 0x24;
        public const ulong SysWrite   = 0x25;
        public const ulong SysSetAbi  = 0x26;

        // DMA cache-coherence control: sys_dma_coherent(virtAddr, setUc)
        // Returns the PTE flag bits for virtAddr; when setUc != 0 the PTE is
        // switched to uncacheable (PCD|PWT) and the TLB entry is flushed.
        public const ulong SysDmaCoherent = 0x0D;
    }
}
