using System;

namespace Microkernel.Abstractions.Rpc
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RpcMethodAttribute : Attribute
    {
        public uint MethodId { get; }

        public RpcMethodAttribute(uint methodId)
        {
            MethodId = methodId;
        }
    }
}
