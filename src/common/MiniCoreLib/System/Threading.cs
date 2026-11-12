using System.Runtime.CompilerServices;

namespace System.Threading
{
    public static unsafe class Thread
    {
        public static long CompilerFence;

        [MethodImpl((MethodImplOptions)8)] // MethodImplOptions.NoInlining
        public static void MemoryBarrier()
        {
            fixed (long* p = &CompilerFence)
            {
                *p = 0;
            }
        }
    }
}
