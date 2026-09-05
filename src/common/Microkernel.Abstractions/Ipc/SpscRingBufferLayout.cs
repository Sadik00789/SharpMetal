using System.Runtime.InteropServices;

namespace Microkernel.Abstractions.Ipc
{
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    public unsafe struct SpscRingBufferHeader
    {
        // Cacheline 0 (0..63): Producer state
        [FieldOffset(0)]
        public ulong Tail;

        [FieldOffset(8)]
        public uint Capacity;      // Must be a power of two

        [FieldOffset(12)]
        public uint ElementSize;

        // Cacheline 1 (64..127): Consumer state (separated to prevent false sharing)
        [FieldOffset(64)]
        public ulong Head;

        // Cacheline 2 (128..191): Buffer pointer and configuration
        [FieldOffset(128)]
        public byte* Buffer;
    }
}
