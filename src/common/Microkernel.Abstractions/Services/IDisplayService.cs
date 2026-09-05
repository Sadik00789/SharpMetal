using Microkernel.Abstractions.Rpc;

namespace Microkernel.Abstractions.Services
{
    [RpcContract]
    public interface IDisplayService
    {
        [RpcMethod(1)]
        ulong RegisterSurface(uint width, uint height, ulong shmCptr);

        [RpcMethod(2)]
        uint CommitSurface(uint surfaceId, uint x, uint y, uint w, uint h);
    }
}
