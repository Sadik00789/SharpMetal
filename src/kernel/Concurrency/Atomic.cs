using System.Runtime.CompilerServices;
using Kernel.Arch.x86_64.Hardware;

namespace Kernel.Concurrency
{
    public static unsafe class Atomic
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Increment(ref int location)
        {
            fixed (int* ptr = &location)
            {
                return Cpu.AtomicIncrement32(ptr);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Decrement(ref int location)
        {
            fixed (int* ptr = &location)
            {
                return Cpu.AtomicDecrement32(ptr);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CompareExchange(ref int location, int value, int comparand)
        {
            fixed (int* ptr = &location)
            {
                return Cpu.AtomicCompareExchange32(ptr, value, comparand);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CompareExchange(ref ulong location, ulong value, ulong comparand)
        {
            fixed (ulong* ptr = &location)
            {
                return Cpu.AtomicCompareExchange64(ptr, value, comparand);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Exchange(ref int location, int value)
        {
            fixed (int* ptr = &location)
            {
                return Cpu.AtomicExchange32(ptr, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint FetchAndAdd(ref uint location, uint delta)
        {
            fixed (uint* ptr = &location)
            {
                return (uint)Cpu.AtomicFetchAndAdd32((int*)ptr, (int)delta);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MemoryBarrier()
        {
            int dummy = 0;
            Cpu.AtomicExchange32(&dummy, 0);
        }
    }
}
