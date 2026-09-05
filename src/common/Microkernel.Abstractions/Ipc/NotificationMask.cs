namespace Microkernel.Abstractions.Ipc
{
    public static class NotificationMask
    {
        public const ulong None = 0x0000000000000000UL;
        public const ulong All  = 0xFFFFFFFFFFFFFFFFUL;

        public static ulong FromBit(int bitIndex)
        {
            if (bitIndex < 0 || bitIndex >= 64) return 0;
            return 1UL << bitIndex;
        }
    }
}
