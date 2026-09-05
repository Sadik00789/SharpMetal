using System.Runtime.InteropServices;

namespace Kernel.Arch.x86_64.Hardware
{
    public static class PortIo
    {
        [DllImport("*")]
        public static extern void Out8(ushort port, byte value);

        [DllImport("*")]
        public static extern byte In8(ushort port);

        [DllImport("*")]
        public static extern void Out16(ushort port, ushort value);

        [DllImport("*")]
        public static extern ushort In16(ushort port);

        [DllImport("*")]
        public static extern void Out32(ushort port, uint value);

        [DllImport("*")]
        public static extern uint In32(ushort port);

        [DllImport("*")]
        public static extern void IoWait();
    }
}
