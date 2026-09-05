using System;
using System.Runtime.InteropServices;

namespace Kernel.Boot
{
    [StructLayout(LayoutKind.Sequential)]
    public struct EfiGuid
    {
        public uint Data1;
        public ushort Data2;
        public ushort Data3;
        public byte Data4_0;
        public byte Data4_1;
        public byte Data4_2;
        public byte Data4_3;
        public byte Data4_4;
        public byte Data4_5;
        public byte Data4_6;
        public byte Data4_7;

        public static readonly EfiGuid GraphicsOutputProtocolGuid = new EfiGuid
        {
            Data1 = 0x9042a9de,
            Data2 = 0x23dc,
            Data3 = 0x4a38,
            Data4_0 = 0x96,
            Data4_1 = 0xfb,
            Data4_2 = 0x7a,
            Data4_3 = 0xde,
            Data4_4 = 0xd0,
            Data4_5 = 0x80,
            Data4_6 = 0x51,
            Data4_7 = 0x6a
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EfiPixelBitmask
    {
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
        public uint ReservedMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiGraphicsOutputModeInformation
    {
        public uint Version;
        public uint HorizontalResolution;
        public uint VerticalResolution;
        public uint PixelFormat;
        public EfiPixelBitmask PixelInformation;
        public uint PixelsPerScanLine;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiGraphicsOutputProtocolMode
    {
        public uint MaxMode;
        public uint Mode;
        public EfiGraphicsOutputModeInformation* Info;
        public nuint SizeOfInfo;
        public ulong FrameBufferBase;
        public nuint FrameBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiGraphicsOutputProtocol
    {
        public IntPtr QueryMode;
        public IntPtr SetMode;
        public IntPtr Blt;
        public EfiGraphicsOutputProtocolMode* Mode;
    }

    public struct GopInfo
    {
        public ulong FrameBufferBase;
        public ulong FrameBufferSize;
        public uint HorizontalResolution;
        public uint VerticalResolution;
        public uint PixelsPerScanLine;
    }
}
