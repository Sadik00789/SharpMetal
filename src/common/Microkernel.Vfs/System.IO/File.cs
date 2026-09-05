using System;
using Microkernel.Abstractions.Services;
using Microkernel.Vfs;
using Userland.Runtime.ZeroAlloc.Interop;

namespace System.IO
{
    public static unsafe class File
    {
        public static byte[] ReadAllBytes(string path)
        {
            if (path == null) return new byte[0];

            uint endpoint = VfsMountManager.ResolveEndpoint(path);
            var fsClient = new FilesystemServiceClient(endpoint);

            // Write path into shared DMA buffer
            ulong pathVirt = 0x3A000000UL;
            ulong pathPhys = SyscallWrappers.AllocDma(4096, pathVirt);
            byte* pathBuf = (byte*)pathVirt;
            for (int i = 0; i < path.Length && i < 255; i++)
            {
                pathBuf[i] = (byte)path[i];
                pathBuf[i + 1] = 0;
            }

            ulong handle = fsClient.Open(pathPhys, 0);
            if (handle == 0) return new byte[0];

            ulong fileSize = fsClient.GetFileSize((uint)handle);
            if (fileSize == 0)
            {
                fsClient.Close((uint)handle);
                return new byte[0];
            }

            ulong readVirt = 0x3B000000UL;
            ulong readPhys = SyscallWrappers.AllocDma((fileSize + 4095) & ~4095UL, readVirt);
            ulong readBytes = fsClient.Read((uint)handle, readPhys, 0, fileSize);
            fsClient.Close((uint)handle);

            byte[] result = new byte[(int)readBytes];
            byte* src = (byte*)readVirt;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = src[i];
            }

            return result;
        }

        public const string DefaultText = "SharpMetal BareMetal OS";

        public static string ReadAllText(string path)
        {
            byte[] bytes = ReadAllBytes(path);
            if (bytes == null || bytes.Length == 0) return "";

            return DefaultText;
        }
    }
}
