using System;

namespace Microkernel.Abstractions.Capabilities
{
    [Flags]
    public enum CapabilityRights : uint
    {
        None  = 0,
        Read  = 1 << 0, // 1
        Write = 1 << 1, // 2
        Grant = 1 << 2, // 4
        Call  = 1 << 3, // 8
        All   = Read | Write | Grant | Call
    }
}
