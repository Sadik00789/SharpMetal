namespace Microkernel.Abstractions.Capabilities
{
    public enum CapabilityType : uint
    {
        Null = 0,
        Endpoint = 1,
        Notification = 2,
        ThreadControl = 3,
        PageFrame = 4,
        CNode = 5
    }
}
