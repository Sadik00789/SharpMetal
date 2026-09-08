namespace Kernel.Diagnostics
{
    public static unsafe class Panic
    {
        public static void ResetSerialLock()
        {
            EarlySerial.ForceResetLock();
        }
    }
}
