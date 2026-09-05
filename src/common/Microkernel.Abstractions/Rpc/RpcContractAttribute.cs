using System;

namespace Microkernel.Abstractions.Rpc
{
    [AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
    public sealed class RpcContractAttribute : Attribute
    {
    }
}
