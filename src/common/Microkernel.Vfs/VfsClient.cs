using System;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Microkernel.Vfs
{
    public static unsafe class VfsClient
    {
        public static ulong Open(string path, uint flags = 0)
        {
            if (path == null) return 0;
            uint endpoint = VfsMountManager.ResolveEndpoint(path);
            var fsClient = new FilesystemServiceClient(endpoint);

            // Write path into shared DMA buffer (0x3A000000UL)
            ulong pathVirt = 0x3A000000UL;
            ulong pathPhys = SyscallWrappers.AllocDma(4096, pathVirt);
            byte* pathBuf = (byte*)pathVirt;
            for (int i = 0; i < path.Length && i < 255; i++)
            {
                pathBuf[i] = (byte)path[i];
                pathBuf[i + 1] = 0;
            }

            return fsClient.Open(pathPhys, flags);
        }

        public static ulong GetFileSize(uint fileHandle)
        {
            var fsClient = new FilesystemServiceClient(VfsMountManager.ResolveEndpoint("/"));
            return fsClient.GetFileSize(fileHandle);
        }

        public static ulong Read(uint fileHandle, ulong outBufferPhys, ulong offset, ulong length)
        {
            var fsClient = new FilesystemServiceClient(VfsMountManager.ResolveEndpoint("/"));
            return fsClient.Read(fileHandle, outBufferPhys, offset, length);
        }

        public static ulong ReadCluster(uint fileHandle, uint clusterIndex, ulong outBufferPhys)
        {
            var fsClient = new FilesystemServiceClient(VfsMountManager.ResolveEndpoint("/"));
            return fsClient.ReadCluster(fileHandle, clusterIndex, outBufferPhys);
        }

        public static uint Close(uint fileHandle)
        {
            var fsClient = new FilesystemServiceClient(VfsMountManager.ResolveEndpoint("/"));
            return fsClient.Close(fileHandle);
        }
    }
}
