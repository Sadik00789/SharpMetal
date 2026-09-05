using Microkernel.Abstractions.Rpc;

namespace Microkernel.Abstractions.Services
{
    [RpcContract]
    public interface IBlockStorageService
    {
        [RpcMethod(1)]
        ulong ReadBlock(ulong lba, ulong shmCptr);

        [RpcMethod(2)]
        ulong WriteBlock(ulong lba, ulong shmCptr);
    }
}
