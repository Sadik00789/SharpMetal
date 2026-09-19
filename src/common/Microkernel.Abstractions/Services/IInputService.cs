using Microkernel.Abstractions.Rpc;

namespace Microkernel.Abstractions.Services
{
    [RpcContract]
    public interface IInputService
    {
        [RpcMethod(1)]
        uint ReadKey();

        /// <summary>
        /// Injects a translated key code (ASCII or control code) into the input
        /// service queue. Used by bus.xhci to forward USB HID boot reports into
        /// the existing PS/2 and serial input path.
        /// </summary>
        [RpcMethod(2)]
        uint InjectKey(uint keyCode);
    }
}
