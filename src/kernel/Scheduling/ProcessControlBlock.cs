using System;
using System.Runtime.InteropServices;
using Kernel.Capabilities;

namespace Kernel.Scheduling
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ProcessControlBlock
    {
        public ulong Id;                     // offset 0
        public ulong PageDirectoryPhysBase;  // offset 8
        public CNode* CSpaceRoot;            // offset 16
        public ProcessControlBlock* Next;    // offset 24
        public Kernel.Memory.Virtual.VirtualMemoryRegion* VmaHead; // offset 32
        public ulong HeapStart;              // offset 40
        public ulong HeapEnd;                // offset 48
        public ulong NextMmapAddress;        // offset 56
        public bool IsUsed;                  // offset 64
    }
}
