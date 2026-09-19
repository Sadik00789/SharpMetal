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

        [RpcMethod(4)]
        uint Socket(uint domain, uint type, uint protocol);

        [RpcMethod(5)]
        uint Bind(uint socketFd, uint ip, ushort port);

        [RpcMethod(6)]
        uint Listen(uint socketFd, uint backlog);

        [RpcMethod(7)]
        uint Accept(uint socketFd, ulong outAddrPhys);

        [RpcMethod(8)]
        uint Connect(uint socketFd, uint ip, ushort port);

        [RpcMethod(9)]
        uint Send(uint socketFd, ulong bufPhys, uint len, uint flags);

        [RpcMethod(10)]
        uint Recv(uint socketFd, ulong bufPhys, uint maxLen, uint flags);

        [RpcMethod(11)]
        uint Close(uint socketFd);
    }
}
