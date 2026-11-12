using System;
using System.Runtime.CompilerServices;
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

            // Write path into dedicated VFS DMA buffer (avoids 0x3A000000 GOP FB collision)
            ulong pathVirt = 0x36000000UL;
            ulong pathPhys = SyscallWrappers.AllocDma(4096, pathVirt);
            if (pathPhys == 0) return new byte[0];
            byte* pathBuf = (byte*)pathVirt;
            pathBuf[0] = 0;
            for (int i = 0; i < path.Length && i < 255; i++)
            {
                pathBuf[i] = (byte)path[i];
                pathBuf[i + 1] = 0;
            }

            ulong handle = fsClient.Open(pathPhys, 0);
            if (handle == 0) return new byte[0];

            ulong fileSize = fsClient.GetFileSize((uint)handle);
            if (fileSize == 0 || fileSize > 0x01000000UL)
            {
                fsClient.Close((uint)handle);
                return new byte[0];
            }

            ulong readVirt = 0x37000000UL;
            ulong readPhys = SyscallWrappers.AllocDma((fileSize + 4095) & ~4095UL, readVirt);
            if (readPhys == 0)
            {
                fsClient.Close((uint)handle);
                return new byte[0];
            }
            ulong readBytes = fsClient.Read((uint)handle, readPhys, 0, fileSize);
            fsClient.Close((uint)handle);

            if (readBytes == 0) return new byte[0];

            byte[] result = new byte[(int)readBytes];
            if (result == null) return new byte[0];
            byte* src = (byte*)readVirt;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = src[i];
            }

            return result;
        }

        public static string ReadAllText(string path)
        {
            byte[] bytes = ReadAllBytes(path);
            if (bytes == null || bytes.Length == 0) return "";

            string template = "";
            IntPtr templateObjPtr = Unsafe.As<string, IntPtr>(ref template);
            fixed (char* templateChars = template)
            {
                int firstCharOff = (int)((byte*)templateChars - (byte*)templateObjPtr);
                if (firstCharOff < 12 || firstCharOff > 32) return "";

                int len = bytes.Length;
                byte[] backing = new byte[firstCharOff + (len * 2) + 2];
                if (backing == null) return "";

                fixed (byte* pRaw = &backing[0])
                {
                    byte* pTemplate = (byte*)templateObjPtr;
                    for (int h = 0; h < firstCharOff; h++)
                    {
                        pRaw[h] = pTemplate[h];
                    }

                    *(int*)(pRaw + firstCharOff - 4) = len;
                    char* dst = (char*)(pRaw + firstCharOff);
                    for (int i = 0; i < len; i++)
                    {
                        dst[i] = (char)bytes[i];
                    }
                    dst[len] = '\0';

                    IntPtr strPtr = (IntPtr)pRaw;
                    return Unsafe.As<IntPtr, string>(ref strPtr);
                }
            }
        }
    }
}
