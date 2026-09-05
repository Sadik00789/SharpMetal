using Microkernel.Abstractions.Rpc;

namespace Microkernel.Abstractions.Services
{
    [RpcContract]
    public interface IFilesystemService
    {
        [RpcMethod(1)]
        ulong Open(ulong pathShmCptr, uint flags);

        [RpcMethod(2)]
        ulong Read(uint fileHandle, ulong outBufferPhys, ulong offset, ulong length);

        [RpcMethod(3)]
        ulong GetFileSize(uint fileHandle);

        [RpcMethod(4)]
        uint Close(uint fileHandle);
    }
}
