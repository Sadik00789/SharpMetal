using Microkernel.Abstractions.Rpc;

namespace Microkernel.Abstractions.Services
{
    [RpcContract]
    public interface INetworkService
    {
        [RpcMethod(1)]
        uint GetMacAddress(ulong outMacBufferPhys);

        [RpcMethod(2)]
        uint SendPacket(ulong packetPhys, uint length);

        [RpcMethod(3)]
        uint ReceivePacket(ulong packetPhys, uint maxLength);
    }
}
