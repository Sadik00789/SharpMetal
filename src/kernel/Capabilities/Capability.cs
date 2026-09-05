using System.Runtime.InteropServices;
using Microkernel.Abstractions.Capabilities;

namespace Kernel.Capabilities
{
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public unsafe struct Capability
    {
        [FieldOffset(0)]
        public void* TargetObject;

        [FieldOffset(8)]
        public CapabilityType Type;

        [FieldOffset(12)]
        public CapabilityRights Rights;

        [FieldOffset(16)]
        public ulong Badge;

        [FieldOffset(24)]
        public ulong Reserved;

        public bool IsNull => TargetObject == null || Type == CapabilityType.Null;
    }
}
