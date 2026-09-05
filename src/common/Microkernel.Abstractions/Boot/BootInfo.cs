using System.Runtime.InteropServices;

namespace Microkernel.Abstractions.Boot
{
    [StructLayout(LayoutKind.Sequential)]
    public struct KernelBootInfo
    {
        public ulong RsdpPhysBase;
        public ulong InitrdPhysBase;
        public ulong InitrdSize;
        public ulong GopPhysBase;
        public ulong GopFbSize;
        public uint GopWidth;
        public uint GopHeight;
        public uint GopPixelsPerScanLine;
        public uint Reserved;
    }
}
