using System;
using System.Runtime.InteropServices;

namespace Roottask
{
    public static unsafe class BootstrapManager
    {
        [DllImport("*", EntryPoint = "DropToUser")]
        public static extern void DropToUser(ulong userRip, ulong userRsp, ulong cr3);

        [DllImport("*", EntryPoint = "EnterUserMode")]
        public static extern void EnterUserMode(ulong userRip, ulong userRsp, ulong cr3);
    }
}
