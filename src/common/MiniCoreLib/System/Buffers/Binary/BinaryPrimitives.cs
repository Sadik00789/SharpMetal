using System.Runtime.CompilerServices;

namespace System.Buffers.Binary
{
    public static class BinaryPrimitives
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReverseEndianness(ushort value)
        {
            return (ushort)((value >> 8) | (value << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReverseEndianness(short value)
        {
            return (short)ReverseEndianness((ushort)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReverseEndianness(uint value)
        {
            return ((value & 0x000000FFU) << 24) |
                   ((value & 0x0000FF00U) << 8)  |
                   ((value & 0x00FF0000U) >> 8)  |
                   ((value & 0xFF000000U) >> 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReverseEndianness(int value)
        {
            return (int)ReverseEndianness((uint)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReverseEndianness(ulong value)
        {
            return ((value & 0x00000000000000FFUL) << 56) |
                   ((value & 0x000000000000FF00UL) << 40) |
                   ((value & 0x0000000000FF0000UL) << 24) |
                   ((value & 0x00000000FF000000UL) << 8)  |
                   ((value & 0x000000FF00000000UL) >> 8)  |
                   ((value & 0x0000FF0000000000UL) >> 24) |
                   ((value & 0x00FF000000000000UL) >> 40) |
                   ((value & 0xFF00000000000000UL) >> 56);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReverseEndianness(long value)
        {
            return (long)ReverseEndianness((ulong)value);
        }
    }
}
