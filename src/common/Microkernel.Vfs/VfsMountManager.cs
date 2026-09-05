using System;

namespace Microkernel.Vfs
{
    public static class VfsMountManager
    {
        private static uint s_rootEndpoint = 11;

        public static void RegisterMount(string prefix, uint endpointCptr)
        {
            s_rootEndpoint = endpointCptr;
        }

        public static uint ResolveEndpoint(string path)
        {
            return s_rootEndpoint;
        }
    }
}
