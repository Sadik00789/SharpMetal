using Microkernel.Abstractions.Rpc;

namespace Microkernel.Abstractions.Services
{
    [RpcContract]
    public interface IPciService
    {
        [RpcMethod(1)]
        ulong FindDevice(uint vendorId, uint deviceId);

        [RpcMethod(2)]
        ulong GetBar(uint bus, uint dev, uint func, uint barIndex);

        [RpcMethod(3)]
        uint TriggerFlr(uint bus, uint dev, uint func);
    }
}
