namespace Kernel.Arch.x86_64.Hardware
{
    public static class Pic8259
    {
        public const ushort MasterCommand = 0x20;
        public const ushort MasterData    = 0x21;
        public const ushort SlaveCommand  = 0xA0;
        public const ushort SlaveData     = 0xA1;

        public static void Disable()
        {
            PortIo.Out8(MasterData, 0xFF);
            PortIo.Out8(SlaveData, 0xFF);
        }

        public static void MaskAll() => Disable();
    }
}
