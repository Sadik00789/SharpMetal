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

        /// <summary>
        /// Exact class/subclass/progIf match returning BAR0, or 0 when absent.
        /// Pass progIf 0xFF to ignore the programming interface. Unlike
        /// <see cref="FindDevice"/> this never falls back to an unrelated BAR,
        /// so callers cannot accidentally program the wrong controller.
        /// </summary>
        [RpcMethod(4)]
        ulong FindDeviceExact(uint baseClass, uint subClass, uint progIf);

        /// <summary>
        /// Returns the enabled interrupt mode for the first class match:
        /// 0 = none, 1 = MSI, 2 = MSI-X.
        /// </summary>
        [RpcMethod(5)]
        uint GetMsiMode(uint baseClass, uint subClass);
    }
}
