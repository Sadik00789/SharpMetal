namespace Microkernel.Abstractions.Ipc
{
    public static class IpcMessageHeader
    {
        public const ulong SyncRpc           = 1;
        public const ulong AsyncNotification = 2;
        public const ulong Reply             = 3;
    }
}
