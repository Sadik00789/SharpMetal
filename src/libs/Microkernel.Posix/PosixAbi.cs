using System.Runtime.CompilerServices;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Microkernel.Posix
{
    public static class PosixAbi
    {
        public const int AbiSharpMetal = 0;
        public const int AbiLinux = 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong SwitchToLinuxAbi()
        {
            return SyscallWrappers.SetAbi(AbiLinux);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong SwitchToDefaultAbi()
        {
            return SyscallWrappers.SetAbi(AbiSharpMetal);
        }
    }
}
