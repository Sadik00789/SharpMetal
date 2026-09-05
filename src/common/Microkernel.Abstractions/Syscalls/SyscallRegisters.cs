using System.Runtime.InteropServices;

namespace Microkernel.Abstractions.Syscalls
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SyscallRegisters
    {
        public ulong Number;
        public ulong Arg1;
        public ulong Arg2;
        public ulong Arg3;
        public ulong Arg4;
        public ulong D0;
        public ulong D1;
        public ulong D2;
        public ulong D3;
        public ulong MsgInfo;
        public ulong ReturnValue;
    }
}
