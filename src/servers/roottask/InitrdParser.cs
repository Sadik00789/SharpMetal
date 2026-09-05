using Microkernel.Abstractions.Initrd;

namespace Roottask
{
    // Re-export or forward to Microkernel.Abstractions.Initrd
    public static class InitrdParserForwarder
    {
        public const ulong ExpectedMagic = InitrdParser.ExpectedMagic;
    }
}
