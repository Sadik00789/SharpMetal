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
        // 64 KiB memory map buffer (>4096 bytes extra headroom upfront)
        [StructLayout(LayoutKind.Sequential, Size = 65536)]
        private struct MemoryMapBuffer { }
        private static MemoryMapBuffer s_mapBuffer;

        // 14 pages (57,344 bytes) to ensure 13 4KiB-aligned pages for PML4/PDPT/PD
        [StructLayout(LayoutKind.Sequential, Size = 14 * 4096)]
        private struct PageTableBuffer { }
        private static PageTableBuffer s_pageTableBuffer;

        // 64 KiB stack buffer for higher-half execution
        [StructLayout(LayoutKind.Sequential, Size = 65536)]
        private struct HighStackBuffer { }
        private static HighStackBuffer s_highStackBuffer;

        // 8 MiB buffer for initial ramdisk (INITRD.IMG)
        [StructLayout(LayoutKind.Sequential, Size = (8 * 1024 * 1024) + 4096)]
        private struct InitrdBuffer { }
        private static InitrdBuffer s_initrdBuffer;

        [UnmanagedCallersOnly(EntryPoint = "EfiMain")]
        public static long Main(IntPtr imageHandle, EfiSystemTable* systemTable)
        {
            // 1. Initialize COM1 Early Serial Port (115200 baud, 8N1)
            EarlySerial.Initialize();

            // 2. Output initialization banner to UEFI ConOut
            if (systemTable != null && systemTable->ConOut != null && systemTable->ConOut->OutputString != null)
            {
                fixed (char* banner = "\r\n" +
                    "=================================================================\r\n" +
                    "   Bare-Metal x86-64 C# Microkernel (Native AOT / UEFI Direct)\r\n" +
                    "   Layer 1 & 2: Firmware Boot, Paging & Higher-Half Handover\r\n" +
                    "=================================================================\r\n\0")
                {
                    systemTable->ConOut->OutputString(systemTable->ConOut, banner);
                }
            }

            // 3. Serial logging
            EarlySerial.WriteLine();
            EarlySerial.WriteLine("=================================================================");
            EarlySerial.WriteLine("   Bare-Metal x86-64 C# Microkernel (Native AOT / UEFI Direct)");
            EarlySerial.WriteLine("   Layer 1 & 2: Firmware Boot, Paging & Higher-Half Handover");
            EarlySerial.WriteLine("=================================================================");
            EarlySerial.Write("[BOOT] ImageHandle: ");
            EarlySerial.WriteHex((ulong)imageHandle);
            EarlySerial.WriteLine();
            EarlySerial.Write("[BOOT] SystemTable: ");
            EarlySerial.WriteHex((ulong)systemTable);
            EarlySerial.WriteLine();

            if (systemTable == null || systemTable->BootServices == null)
            {
                EarlySerial.WriteLine("[ERROR] Invalid UEFI SystemTable or BootServices!");
                PortIo.Out8(0xF4, 0x01);
                return 1;
            }

            // 4. Locate EFI_GRAPHICS_OUTPUT_PROTOCOL (GOP)
            EfiGuid gopGuid = new EfiGuid
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
            void* gopInterface = null;
            long gopStatus = systemTable->BootServices->LocateProtocol(&gopGuid, IntPtr.Zero, &gopInterface);

            EarlySerial.Write("[GOP] LocateProtocol status: ");
            EarlySerial.WriteHex((ulong)gopStatus);
            EarlySerial.WriteLine();

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

                    EarlySerial.Write("[GOP] Framebuffer Physical Base: ");
                    EarlySerial.WriteHex(KernelHigh.GopPhysBase);
                    EarlySerial.WriteLine();
                    EarlySerial.Write("[GOP] Framebuffer Size: ");
                    EarlySerial.WriteHex(KernelHigh.GopFbSize);
                    EarlySerial.WriteLine();
                    EarlySerial.Write("[GOP] Resolution: ");
                    EarlySerial.WriteDec(KernelHigh.GopWidth);
                    EarlySerial.Write("x");
                    EarlySerial.WriteDec(KernelHigh.GopHeight);
                    EarlySerial.Write(" (Pitch: ");
                    EarlySerial.WriteDec(KernelHigh.GopPixelsPerScanLine);
                    EarlySerial.WriteLine(")");
                }
            }
            else
            {
                EarlySerial.WriteLine("[WARN] GOP Protocol could not be located.");
            }

            // 4b. Load INITRD.IMG from FAT32 boot volume
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
                            long openStatus = rootDir->Open(rootDir, &initrdFile, initrdPath, 1UL /* EFI_FILE_MODE_READ */, 0);
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

                                        EarlySerial.Write("[INITRD] Loaded ramdisk from ESP at physical: ");
                                        EarlySerial.WriteHex(KernelHigh.InitrdPhysBase);
                                        EarlySerial.Write(" (Size: ");
                                        EarlySerial.WriteDec((long)KernelHigh.InitrdSize);
                                        EarlySerial.WriteLine(" bytes)");
                                    }
                                }
                                initrdFile->Close(initrdFile);
                            }
                            else
                            {
                                EarlySerial.WriteLine("[WARN] Could not open EFI\\BOOT\\INITRD.IMG");
                            }
                        }
                        rootDir->Close(rootDir);
                    }
                }
            }

            // 4c. Locate ACPI 2.0 / 1.0 RSDP Table Pointer from systemTable->ConfigurationTable
            // ACPI 2.0: { 0x8868e871, 0xe4f1, 0x11d3, { 0xbc, 0x22, 0x00, 0x80, 0xc7, 0x3c, 0x88, 0x81 } }
            // ACPI 1.0: { 0xeb9d2d30, 0x2d88, 0x11d3, { 0x9a, 0x16, 0x00, 0x90, 0x27, 0x3f, 0xc1, 0x4d } }
            if (systemTable != null && (void*)systemTable->ConfigurationTable != null)
            {
                EfiConfigurationTable* configTables = (EfiConfigurationTable*)systemTable->ConfigurationTable;
                nuint numEntries = systemTable->NumberOfTableEntries;
                ulong rsdp2Phys = 0;
                ulong rsdp1Phys = 0;

                for (nuint i = 0; i < numEntries; i++)
                {
                    EfiConfigurationTable* entry = &configTables[i];
                    if (entry->VendorGuid.Data1 == 0x8868E871 &&
                        entry->VendorGuid.Data2 == 0xE4F1 &&
                        entry->VendorGuid.Data3 == 0x11D3 &&
                        entry->VendorGuid.Data4_0 == 0xBC && entry->VendorGuid.Data4_1 == 0x22 &&
                        entry->VendorGuid.Data4_2 == 0x00 && entry->VendorGuid.Data4_3 == 0x80 &&
                        entry->VendorGuid.Data4_4 == 0xC7 && entry->VendorGuid.Data4_5 == 0x3C &&
                        entry->VendorGuid.Data4_6 == 0x88 && entry->VendorGuid.Data4_7 == 0x81)
                    {
                        rsdp2Phys = (ulong)entry->VendorTable;
                        break;
                    }

                    if (entry->VendorGuid.Data1 == 0xEB9D2D30 &&
                        entry->VendorGuid.Data2 == 0x2D88 &&
                        entry->VendorGuid.Data3 == 0x11D3 &&
                        entry->VendorGuid.Data4_0 == 0x9A && entry->VendorGuid.Data4_1 == 0x16 &&
                        entry->VendorGuid.Data4_2 == 0x00 && entry->VendorGuid.Data4_3 == 0x90 &&
                        entry->VendorGuid.Data4_4 == 0x27 && entry->VendorGuid.Data4_5 == 0x3F &&
                        entry->VendorGuid.Data4_6 == 0xC1 && entry->VendorGuid.Data4_7 == 0x4D)
                    {
                        rsdp1Phys = (ulong)entry->VendorTable;
                    }
                }

                ulong rsdpPhys = rsdp2Phys != 0 ? rsdp2Phys : rsdp1Phys;
                KernelHigh.RsdpPhysBase = rsdpPhys;
                EarlySerial.Write("[ACPI] Located RSDP at physical: ");
                EarlySerial.WriteHex(rsdpPhys);
                EarlySerial.WriteLine();
            }

            fixed (MemoryMapBuffer* pMap = &s_mapBuffer)
            fixed (PageTableBuffer* pPt = &s_pageTableBuffer)
            fixed (HighStackBuffer* pStk = &s_highStackBuffer)
            {
                // 5. Query initial memory map for physical memory managers
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
                    ulong totalRam = UefiMemoryParser.GetTotalUsableMemory(pMapBuffer, initialMapSize, initialDescSize);
                    EarlySerial.Write("[MEM] Total Usable RAM: ");
                    EarlySerial.WriteHex(totalRam);
                    EarlySerial.WriteLine();

                    if (UefiMemoryParser.FindLargestConventionalRegion(pMapBuffer, initialMapSize, initialDescSize, out ulong regionStart, out ulong pageCount))
                    {
                        EarlySerial.Write("[PMM] Largest Conventional Region: ");
                        EarlySerial.WriteHex(regionStart);
                        EarlySerial.Write(" (Pages: ");
                        EarlySerial.WriteDec((long)pageCount);
                        EarlySerial.WriteLine(")");

                        if (pageCount >= 8192) // 32 MiB = 8192 pages
                        {
                            DmaArenaAllocator.Initialize(regionStart, 32 * 1024 * 1024);
                            EarlySerial.Write("[DMA] 32 MiB DMA Arena reserved at: ");
                            EarlySerial.WriteHex(regionStart);
                            EarlySerial.WriteLine();

                            // Allocate bitmap for PageFrameAllocator from DMA arena
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

                            EarlySerial.Write("[PMM] Physical Frame Allocator configured. Total frames: ");
                            EarlySerial.WriteDec((long)totalFrames);
                            EarlySerial.WriteLine();
                        }
                    }
                }

                // 6. Build 4-level page tables with Identity, HHDM, and PAT WC on GOP Framebuffer
                ulong rawPt = (ulong)(byte*)pPt;
                ulong alignedPt = (rawPt + 4095) & ~4095UL;
                ulong pml4Phys = VirtualMemorySpace.CreateKernelSpace(
                    (ulong*)alignedPt,
                    KernelHigh.GopPhysBase,
                    KernelHigh.GopFbSize);

                EarlySerial.Write("[PAGING] Kernel PML4 created at: ");
                EarlySerial.WriteHex(pml4Phys);
                EarlySerial.WriteLine();

                // 7. Configure IA32_PAT MSR (PA4 = 0x01 Write-Combining)
                ulong patVal = PatManager.Initialize();
                EarlySerial.Write("[PAT] IA32_PAT programmed. MSR 0x277: ");
                EarlySerial.WriteHex(patVal);
                EarlySerial.WriteLine();

                // 8. ExitBootServices with >= 4096 bytes headroom upfront
                // Strictly NO allocations or logging between GetMemoryMap and ExitBootServices
                bool exitSuccess = ExitBootServicesHelper.Exit(
                    imageHandle,
                    systemTable->BootServices,
                    pMapBuffer,
                    65536,
                    out nuint finalMapKey,
                    out nuint finalDescSize);

                if (!exitSuccess)
                {
                    EarlySerial.WriteLine("[ERROR] ExitBootServices failed after retries!");
                    PortIo.Out8(0xF4, 0x01);
                    return 1;
                }

                // 9. Now in bare-metal control! Disable hardware interrupts
                Cpu.DisableInterrupts();
                EarlySerial.WriteLine("[BOOT] ExitBootServices succeeded. UEFI terminated. Interrupts disabled.");

                // 10. High Entry Address Calculation:
                // Convert &KernelHigh.KernelMainHigh to its canonical higher-half address (add Hhdm.Base)
                delegate* unmanaged[Cdecl]<void> lowEntry = &KernelHigh.KernelMainHigh;
                ulong highEntry = (ulong)lowEntry + Hhdm.Base;

                // Compute 16-byte aligned high stack top in HHDM
                ulong stackPhys = (ulong)(byte*)pStk;
                ulong highStackTop = (stackPhys + 65536 + Hhdm.Base) & ~15UL;

                EarlySerial.Write("[BOOT] High Entry Address: ");
                EarlySerial.WriteHex(highEntry);
                EarlySerial.WriteLine();
                EarlySerial.Write("[BOOT] High Stack Top: ");
                EarlySerial.WriteHex(highStackTop);
                EarlySerial.WriteLine();

                // 11. Transition CPU into Higher-Half:
                // Switch CR3, 16-byte align RSP with 40-byte shadow space, and jump to KernelMainHigh
                Cpu.SwitchToHigherHalf(pml4Phys, highStackTop, highEntry);
            }

            return 0;
        }
    }
}
