using Microkernel.Abstractions.Rpc;

namespace Microkernel.Abstractions.Services
{
    [RpcContract]
    public interface IInputService
    {
        [RpcMethod(1)]
        uint ReadKey();
    }
}
