using System;
using System.Runtime.InteropServices;

namespace Kernel.Boot
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiBootServices
    {
        public EfiTableHeader Hdr;

        // Task Priority Services
        public IntPtr RaiseTPL;
        public IntPtr RestoreTPL;

        // Memory Services
        public IntPtr AllocatePages;
        public IntPtr FreePages;
        public delegate* unmanaged[Cdecl]<nuint*, EfiMemoryDescriptor*, nuint*, nuint*, uint*, long> GetMemoryMap;
        public IntPtr AllocatePool;
        public IntPtr FreePool;

        // Event & Timer Services
        public IntPtr CreateEvent;
        public IntPtr SetTimer;
        public IntPtr WaitForEvent;
        public IntPtr SignalEvent;
        public IntPtr CloseEvent;
        public IntPtr CheckEvent;

        // Protocol Handler Services
        public IntPtr InstallProtocolInterface;
        public IntPtr ReinstallProtocolInterface;
        public IntPtr UninstallProtocolInterface;
        public delegate* unmanaged[Cdecl]<IntPtr, EfiGuid*, void**, long> HandleProtocol;
        public IntPtr Reserved;
        public IntPtr RegisterProtocolNotify;
        public IntPtr LocateHandle;
        public IntPtr LocateDevicePath;
        public IntPtr InstallConfigurationTable;

        // Image Services
        public IntPtr LoadImage;
        public IntPtr StartImage;
        public IntPtr Exit;
        public IntPtr UnloadImage;
        public delegate* unmanaged[Cdecl]<IntPtr, nuint, long> ExitBootServices;

        // Miscellaneous Services
        public IntPtr GetNextMonotonicCount;
        public IntPtr Stall;
        public IntPtr SetWatchdogTimer;

        // Driver Support Services
        public IntPtr ConnectController;
        public IntPtr DisconnectController;

        // Open and Close Protocol Services
        public IntPtr OpenProtocol;
        public IntPtr CloseProtocol;
        public IntPtr OpenProtocolInformation;

        // Library Services
        public IntPtr ProtocolsPerHandle;
        public IntPtr LocateHandleBuffer;
        public delegate* unmanaged[Cdecl]<EfiGuid*, IntPtr, void**, long> LocateProtocol;
        public IntPtr InstallMultipleProtocolInterfaces;
        public IntPtr UninstallMultipleProtocolInterfaces;

        // 32-bit CRC Services
        public IntPtr CalculateCrc32;

        // Miscellaneous Services
        public IntPtr CopyMem;
        public IntPtr SetMem;
        public IntPtr CreateEventEx;
    }

    public static unsafe class ExitBootServicesHelper
    {
        public static bool Exit(
            IntPtr imageHandle,
            EfiBootServices* bootServices,
            byte* mapBuffer,
            nuint mapBufferSize,
            out nuint finalMapKey,
            out nuint finalDescSize)
        {
            finalMapKey = 0;
            finalDescSize = 0;

            if (bootServices == null || bootServices->GetMemoryMap == null || bootServices->ExitBootServices == null)
            {
                return false;
            }

            // Retry loop up to 5 attempts as per UEFI specification
            for (int attempt = 0; attempt < 5; attempt++)
            {
                nuint currentMapSize = mapBufferSize;
                nuint currentMapKey = 0;
                nuint currentDescSize = 0;
                uint descVersion = 0;

                // 1. Get memory map
                // Strictly ZERO allocations and ZERO logging between GetMemoryMap and ExitBootServices
                long status = bootServices->GetMemoryMap(
                    &currentMapSize,
                    (EfiMemoryDescriptor*)mapBuffer,
                    &currentMapKey,
                    &currentDescSize,
                    &descVersion);

                if (status != 0)
                {
                    return false;
                }

                // 2. Immediately call ExitBootServices with the retrieved MapKey
                status = bootServices->ExitBootServices(imageHandle, currentMapKey);
                if (status == 0) // EFI_SUCCESS
                {
                    finalMapKey = currentMapKey;
                    finalDescSize = currentDescSize;
                    return true;
                }

                // If ExitBootServices failed, loop to query memory map again with updated MapKey
            }

            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiLoadedImageProtocol
    {
        public uint Revision;
        public IntPtr ParentHandle;
        public IntPtr SystemTable;
        public IntPtr DeviceHandle;
        public IntPtr FilePath;
        public IntPtr Reserved;
        public uint LoadOptionsSize;
        public void* LoadOptions;
        public void* ImageBase;
        public ulong ImageSize;
        public uint ImageCodeType;
        public uint ImageDataType;
        public IntPtr Unload;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiSimpleFileSystemProtocol
    {
        public ulong Revision;
        public delegate* unmanaged[Cdecl]<EfiSimpleFileSystemProtocol*, EfiFileProtocol**, long> OpenVolume;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiFileProtocol
    {
        public ulong Revision;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, EfiFileProtocol**, char*, ulong, ulong, long> Open;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, long> Close;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, long> Delete;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, nuint*, void*, long> Read;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, nuint*, void*, long> Write;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, ulong*, long> GetPosition;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, ulong, long> SetPosition;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, EfiGuid*, nuint*, void*, long> GetInfo;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, EfiGuid*, nuint*, void*, long> SetInfo;
        public delegate* unmanaged[Cdecl]<EfiFileProtocol*, long> Flush;
    }
}

