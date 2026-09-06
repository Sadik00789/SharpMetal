using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Diagnostics;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;

namespace Kernel.Boot
{
    [StructLayout(LayoutKind.Sequential)]
    public struct EfiTableHeader
    {
        public ulong Signature;
        public uint Revision;
        public uint HeaderSize;
        public uint Crc32;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiSimpleTextOutputProtocol
    {
        public IntPtr Reset;
        public delegate* unmanaged[Cdecl]<EfiSimpleTextOutputProtocol*, char*, long> OutputString;
        public IntPtr TestString;
        public IntPtr QueryMode;
        public IntPtr SetMode;
        public IntPtr SetAttribute;
        public IntPtr ClearScreen;
        public IntPtr SetCursorPosition;
        public IntPtr EnableCursor;
        public IntPtr Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiSystemTable
    {
        public EfiTableHeader Hdr;
        public char* FirmwareVendor;
        public uint FirmwareRevision;
        public IntPtr ConsoleInHandle;
        public IntPtr ConIn;
        public IntPtr ConsoleOutHandle;
        public EfiSimpleTextOutputProtocol* ConOut;
        public IntPtr StandardErrorHandle;
        public IntPtr StdErr;
        public IntPtr RuntimeServices;
        public EfiBootServices* BootServices;
        public nuint NumberOfTableEntries;
        public IntPtr ConfigurationTable;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EfiConfigurationTable
    {
        public EfiGuid VendorGuid;
        public void* VendorTable;
    }

    public static unsafe class EfiMain
    {
        [StructLayout(LayoutKind.Sequential, Size = 65536)]
        private struct MemoryMapBuffer { }
        private static MemoryMapBuffer s_mapBuffer;

        [StructLayout(LayoutKind.Sequential, Size = 40 * 4096)]
        private struct PageTableBuffer { }
        private static PageTableBuffer s_pageTableBuffer;

        [StructLayout(LayoutKind.Sequential, Size = 65536)]
        private struct HighStackBuffer { }
        private static HighStackBuffer s_highStackBuffer;

        [StructLayout(LayoutKind.Sequential, Size = (8 * 1024 * 1024) + 4096)]
        private struct InitrdBuffer { }
        private static InitrdBuffer s_initrdBuffer;

        private static void PrintScreen(EfiSystemTable* st, string msg)
        {
            if (st != null && st->ConOut != null && st->ConOut->OutputString != null)
            {
                fixed (char* p = msg)
                {
                    st->ConOut->OutputString(st->ConOut, p);
                }
            }
        }

        private static void PrintHex(EfiSystemTable* st, ulong value)
        {
            char* hexStr = stackalloc char[21];
            hexStr[0] = '0';
            hexStr[1] = 'x';
            for (int i = 0; i < 16; i++)
            {
                int shift = (15 - i) * 4;
                byte nibble = (byte)((value >> shift) & 0xF);
                hexStr[2 + i] = (char)(nibble < 10 ? ('0' + nibble) : ('A' + (nibble - 10)));
            }
            hexStr[18] = '\r';
            hexStr[19] = '\n';
            hexStr[20] = '\0';
            if (st != null && st->ConOut != null && st->ConOut->OutputString != null)
            {
                st->ConOut->OutputString(st->ConOut, hexStr);
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "EfiMain")]
        public static long Main(IntPtr imageHandle, EfiSystemTable* systemTable)
        {
            EarlySerial.Initialize();

            PrintScreen(systemTable, "\r\n[1/8] Booting SharpMetal x86-64 Microkernel...\r\n\0");

            if (systemTable == null || systemTable->BootServices == null)
            {
                PrintScreen(systemTable, "[FATAL] Invalid UEFI BootServices!\r\n\0");
                return 1;
            }

            // 1. Locate GOP
            PrintScreen(systemTable, "[2/8] Locating UEFI GOP Framebuffer: \0");
            EfiGuid gopGuid = new EfiGuid
            {
                Data1 = 0x9042a9de, Data2 = 0x23dc, Data3 = 0x4a38,
                Data4_0 = 0x96, Data4_1 = 0xfb, Data4_2 = 0x7a, Data4_3 = 0xde,
                Data4_4 = 0xd0, Data4_5 = 0x80, Data4_6 = 0x51, Data4_7 = 0x6a
            };
            void* gopInterface = null;
            long gopStatus = systemTable->BootServices->LocateProtocol(&gopGuid, IntPtr.Zero, &gopInterface);

            if (gopStatus == 0 && gopInterface != null)
            {
                EfiGraphicsOutputProtocol* gop = (EfiGraphicsOutputProtocol*)gopInterface;
                if (gop->Mode != null && gop->Mode->Info != null)
                {
                    KernelHigh.GopPhysBase = gop->Mode->FrameBufferBase;
                    KernelHigh.GopFbSize = gop->Mode->FrameBufferSize;
                    KernelHigh.GopWidth = gop->Mode->Info->HorizontalResolution;
                    KernelHigh.GopHeight = gop->Mode->Info->VerticalResolution;
                    KernelHigh.GopPixelsPerScanLine = gop->Mode->Info->PixelsPerScanLine;
                    PrintHex(systemTable, KernelHigh.GopPhysBase);
            delegate* unmanaged[Cdecl]<void> lowEntry = &KernelHigh.KernelMainHigh;
            PrintScreen(systemTable, "[ADDR] Kernel Physical Entry: ");
            PrintHex(systemTable, (ulong)lowEntry);

                }
            }
            else
            {
                PrintScreen(systemTable, "NOT FOUND\r\n\0");
            }

            // 2. Load INITRD
            PrintScreen(systemTable, "[3/8] Loading INITRD.IMG from boot volume...\r\n\0");
            EfiGuid loadedImageGuid = new EfiGuid
            {
                Data1 = 0x5B1B31A1, Data2 = 0x9562, Data3 = 0x11D2,
                Data4_0 = 0x8E, Data4_1 = 0x3F, Data4_2 = 0x00, Data4_3 = 0xA0,
                Data4_4 = 0xC9, Data4_5 = 0x69, Data4_6 = 0x72, Data4_7 = 0x3B
            };
            EfiGuid fsGuid = new EfiGuid
            {
                Data1 = 0x964E5B22, Data2 = 0x6459, Data3 = 0x11D2,
                Data4_0 = 0x8E, Data4_1 = 0x39, Data4_2 = 0x00, Data4_3 = 0xA0,
                Data4_4 = 0xC9, Data4_5 = 0x69, Data4_6 = 0x72, Data4_7 = 0x3B
            };

            void* loadedImageInterface = null;
            long liStatus = systemTable->BootServices->HandleProtocol(imageHandle, &loadedImageGuid, &loadedImageInterface);
            if (liStatus == 0 && loadedImageInterface != null)
            {
                EfiLoadedImageProtocol* loadedImage = (EfiLoadedImageProtocol*)loadedImageInterface;
                void* fsInterface = null;
                long fsStatus = systemTable->BootServices->HandleProtocol(loadedImage->DeviceHandle, &fsGuid, &fsInterface);
                if (fsStatus == 0 && fsInterface != null)
                {
                    EfiSimpleFileSystemProtocol* fs = (EfiSimpleFileSystemProtocol*)fsInterface;
                    EfiFileProtocol* rootDir = null;
                    long ovStatus = fs->OpenVolume(fs, &rootDir);
                    if (ovStatus == 0 && rootDir != null)
                    {
                        EfiFileProtocol* initrdFile = null;
                        fixed (char* initrdPath = "EFI\\BOOT\\INITRD.IMG\0")
                        {
                            long openStatus = rootDir->Open(rootDir, &initrdFile, initrdPath, 1UL, 0);
                            if (openStatus == 0 && initrdFile != null)
                            {
                                fixed (InitrdBuffer* pInitrd = &s_initrdBuffer)
                                {
                                    byte* alignedInitrd = (byte*)(((ulong)(byte*)pInitrd + 4095) & ~4095UL);
                                    nuint readBytes = (nuint)(8 * 1024 * 1024);
                                    long readStatus = initrdFile->Read(initrdFile, &readBytes, (void*)alignedInitrd);
                                    if (readStatus == 0 && readBytes > 0)
                                    {
                                        KernelHigh.InitrdPhysBase = (ulong)alignedInitrd;
                                        KernelHigh.InitrdSize = (ulong)readBytes;
                                    }
                                }
                                initrdFile->Close(initrdFile);
                            }
                        }
                        rootDir->Close(rootDir);
                    }
                }
            }

            // 3. Locate ACPI RSDP
            PrintScreen(systemTable, "[4/8] Locating ACPI RSDP...\r\n\0");
            if (systemTable != null && (void*)systemTable->ConfigurationTable != null)
            {
                EfiConfigurationTable* configTables = (EfiConfigurationTable*)systemTable->ConfigurationTable;
                nuint numEntries = systemTable->NumberOfTableEntries;
                for (nuint i = 0; i < numEntries; i++)
                {
                    EfiConfigurationTable* entry = &configTables[i];
                    if (entry->VendorTable != null)
                    {
                        if (entry->VendorGuid.Data1 == 0x8868E871 && entry->VendorGuid.Data2 == 0xE4F1)
                        {
                            KernelHigh.RsdpPhysBase = (ulong)entry->VendorTable;
                            break;
                        }
                        if (entry->VendorGuid.Data1 == 0xEB9D2D30 && entry->VendorGuid.Data2 == 0x2D88)
                        {
                            KernelHigh.RsdpPhysBase = (ulong)entry->VendorTable;
                        }
                    }
                }
            }

            fixed (MemoryMapBuffer* pMap = &s_mapBuffer)
            fixed (PageTableBuffer* pPt = &s_pageTableBuffer)
            fixed (HighStackBuffer* pStk = &s_highStackBuffer)
            {
                // 4. Memory map
                PrintScreen(systemTable, "[5/8] Querying Memory Map & PMM...\r\n\0");
                byte* pMapBuffer = (byte*)pMap;
                nuint initialMapSize = 65536;
                nuint initialMapKey = 0;
                nuint initialDescSize = 0;
                uint initialDescVersion = 0;

                long mapStatus = systemTable->BootServices->GetMemoryMap(
                    &initialMapSize,
                    (EfiMemoryDescriptor*)pMapBuffer,
                    &initialMapKey,
                    &initialDescSize,
                    &initialDescVersion);

                if (mapStatus == 0)
                {
                    if (UefiMemoryParser.FindLargestConventionalRegionBelow4G(pMapBuffer, initialMapSize, initialDescSize, out ulong regionStart, out ulong pageCount))
                    {
                        if (pageCount >= 8192)
                        {
                            DmaArenaAllocator.Initialize(regionStart, 32 * 1024 * 1024);
                            ulong maxPhys = UefiMemoryParser.GetMaxPhysicalAddress(pMapBuffer, initialMapSize, initialDescSize);
                            ulong totalFrames = (maxPhys + 4095) / 4096;
                            ulong bitmapBytes = (totalFrames + 7) / 8;
                            ulong bitmapPhys = DmaArenaAllocator.Allocate(bitmapBytes, 4096);
                            ulong pmmStart = regionStart + (32 * 1024 * 1024);
                            ulong pmmPages = pageCount - 8192;

                            KernelHigh.PmmBitmapPhys = bitmapPhys;
                            KernelHigh.PmmTotalFrames = totalFrames;
                            KernelHigh.PmmStart = pmmStart;
                            KernelHigh.PmmPages = pmmPages;

                            // Register all conventional memory regions up to 16 GiB
                            nuint numDescriptors = initialMapSize / initialDescSize;
                            int regIdx = 0;

                            if (pmmPages > 0)
                            {
                                KernelHigh.UsableMemoryMap.RegionStarts[regIdx] = pmmStart;
                                KernelHigh.UsableMemoryMap.RegionPageCounts[regIdx] = pmmPages;
                                regIdx++;
                            }

                            for (nuint d = 0; d < numDescriptors && regIdx < KernelHigh.BootMemoryMap.MaxRegions; d++)
                            {
                                EfiMemoryDescriptor* desc = (EfiMemoryDescriptor*)(pMapBuffer + (d * initialDescSize));
                                if (desc->Type == (uint)EfiMemoryType.EfiConventionalMemory)
                                {
                                    if (desc->PhysicalStart == regionStart) continue;

                                    if (desc->PhysicalStart < 0x4_0000_0000UL) // below 16 GiB
                                    {
                                        ulong start = desc->PhysicalStart;
                                        ulong pages = desc->NumberOfPages;
                                        ulong end = start + (pages * 4096);
                                        if (end > 0x4_0000_0000UL)
                                        {
                                            pages = (0x4_0000_0000UL - start) / 4096;
                                        }

                                        if (pages > 0)
                                        {
                                            KernelHigh.UsableMemoryMap.RegionStarts[regIdx] = start;
                                            KernelHigh.UsableMemoryMap.RegionPageCounts[regIdx] = pages;
                                            regIdx++;
                                        }
                                    }
                                }
                            }
                            KernelHigh.UsableMemoryMap.RegionCount = regIdx;
                        }
                    }
                }

                // 5. Page Tables
                PrintScreen(systemTable, "[6/8] Building 4-Level Page Tables...\r\n\0");
                ulong rawPt = (ulong)(byte*)pPt;
                ulong alignedPt = (rawPt + 4095) & ~4095UL;
                ulong pml4Phys = VirtualMemorySpace.CreateKernelSpace(
                    (ulong*)alignedPt,
                    KernelHigh.GopPhysBase,
                    KernelHigh.GopFbSize);

                // 6. PAT MSR
                PrintScreen(systemTable, "[7/8] Programming IA32_PAT MSR...\r\n\0");
                PatManager.Initialize();

                // 7. ExitBootServices
                PrintScreen(systemTable, "[8/8] Calling ExitBootServices (Terminating UEFI)...\r\n\0");
                bool exitSuccess = ExitBootServicesHelper.Exit(
                    imageHandle,
                    systemTable->BootServices,
                    pMapBuffer,
                    65536,
                    out nuint finalMapKey,
                    out nuint finalDescSize);

                if (!exitSuccess)
                {
                    PrintScreen(systemTable, "[FATAL ERROR] ExitBootServices failed!\r\n\0");
                    while (true) { }
                }

                Cpu.DisableInterrupts();

                delegate* unmanaged[Cdecl]<void> lowEntry = &KernelHigh.KernelMainHigh;
                ulong highEntry = (ulong)lowEntry + Hhdm.Base;
                ulong stackPhys = (ulong)(byte*)pStk;
                ulong highStackTop = (stackPhys + 65536 + Hhdm.Base) & ~15UL;

                Cpu.SwitchToHigherHalf(pml4Phys, highStackTop, highEntry);
            }

            return 0;
        }
    }
}
