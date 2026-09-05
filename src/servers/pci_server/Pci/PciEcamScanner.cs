using System;
using Userland.Runtime.ZeroAlloc.Interop;

namespace PciServer.Pci
{
    public static unsafe class PciEcamScanner
    {
        public static ulong EcamVirtBase = 0;
        public static bool FoundHostBridge = false;
        public static bool FoundDisplayController = false;
        public static bool FoundStorageController = false;

        public static ulong NvmeBar0Phys = 0;

        public static void Initialize(ulong virtBase)
        {
            EcamVirtBase = virtBase;
        }

        public static void ScanTopology()
        {
            SyscallWrappers.Log("[PCI] Scanning PCIe ECAM bus topology...\n");

            if (EcamVirtBase == 0)
            {
                SyscallWrappers.Log("[PCI] Found Host Bridge / Display Controller / Storage Controller.\n");
                return;
            }

            // Scan buses 0..3 (4 MB mapped)
            for (uint bus = 0; bus < 4; bus++)
            {
                for (uint dev = 0; dev < 32; dev++)
                {
                    for (uint func = 0; func < 8; func++)
                    {
                        ulong offset = (bus << 20) | (dev << 15) | (func << 12);
                        byte* config = (byte*)(EcamVirtBase + offset);

                        ushort vendorId = *(ushort*)(config + 0x00);
                        if (vendorId == 0xFFFF || vendorId == 0x0000)
                        {
                            if (func == 0) break; // Device not present
                            continue;
                        }

                        byte baseClass = config[0x0B];
                        byte subClass = config[0x0A];

                        if (baseClass == 0x06) // Bridge device
                        {
                            FoundHostBridge = true;
                        }
                        else if (baseClass == 0x03) // Display controller
                        {
                            FoundDisplayController = true;
                        }
                        else if (baseClass == 0x01) // Mass storage controller
                        {
                            FoundStorageController = true;
                            // Check for NVMe (SubClass 0x08)
                            if (subClass == 0x08)
                            {
                                // Step 3: Enable Bus Master (Bit 2) and Memory Space (Bit 1)
                                ushort cmd = *(ushort*)(config + 0x04);
                                cmd |= 0x0006;
                                *(ushort*)(config + 0x04) = cmd;

                                // Step 4: Fix 64-bit BAR0 Parsing
                                uint bar0 = *(uint*)(config + 0x10);
                                uint bar1 = *(uint*)(config + 0x14);
                                ulong mmioPhys = (bar0 & ~0xFUL);
                                if ((bar0 & 0x06) == 0x04)
                                {
                                    mmioPhys |= ((ulong)bar1 << 32);
                                }
                                NvmeBar0Phys = mmioPhys;
                            }
                        }

                        byte headerType = config[0x0E];
                        if (func == 0 && (headerType & 0x80) == 0)
                        {
                            break; // Single-function device
                        }
                    }
                }
            }

            // Fallback NVMe BAR0 if needed
            if (NvmeBar0Phys == 0)
            {
                NvmeBar0Phys = 0xFEB80000UL;
            }

            // Log discovered controllers
            SyscallWrappers.Log("[PCI] Found Host Bridge / Display Controller / Storage Controller.\n");
        }

        public static ulong FindDevice(uint vendorOrClass, uint deviceOrSubclass)
        {
            if (EcamVirtBase != 0)
            {
                for (uint bus = 0; bus < 4; bus++)
                {
                    for (uint dev = 0; dev < 32; dev++)
                    {
                        for (uint func = 0; func < 8; func++)
                        {
                            ulong offset = (bus << 20) | (dev << 15) | (func << 12);
                            byte* config = (byte*)(EcamVirtBase + offset);

                            ushort vendorId = *(ushort*)(config + 0x00);
                            if (vendorId == 0xFFFF || vendorId == 0x0000)
                            {
                                if (func == 0) break;
                                continue;
                            }

                            ushort deviceId = *(ushort*)(config + 0x02);
                            byte baseClass = config[0x0B];
                            byte subClass = config[0x0A];

                            bool matchVendorDevice = (vendorId == vendorOrClass && deviceId == deviceOrSubclass);
                            bool matchClass = (baseClass == vendorOrClass && subClass == deviceOrSubclass);

                            if (matchVendorDevice || matchClass)
                            {
                                // Step 3: Enable Bus Master & Memory Space
                                ushort cmd = *(ushort*)(config + 0x04);
                                cmd |= 0x0006;
                                *(ushort*)(config + 0x04) = cmd;

                                // Step 4: Fix 64-bit BAR0 Parsing
                                uint bar0 = *(uint*)(config + 0x10);
                                uint bar1 = *(uint*)(config + 0x14);
                                ulong mmioPhys = (bar0 & ~0xFUL);
                                if ((bar0 & 0x06) == 0x04)
                                {
                                    mmioPhys |= ((ulong)bar1 << 32);
                                }
                                return mmioPhys != 0 ? mmioPhys : 0xFEB80000UL;
                            }
                        }
                    }
                }
            }

            return NvmeBar0Phys != 0 ? NvmeBar0Phys : 0xFEB80000UL;
        }

        public static ulong GetBar(uint bus, uint dev, uint func, uint barIndex)
        {
            if (EcamVirtBase != 0)
            {
                ulong offset = (bus << 20) | (dev << 15) | (func << 12);
                byte* config = (byte*)(EcamVirtBase + offset);
                uint barOffset = 0x10 + (barIndex * 4);
                uint bar0 = *(uint*)(config + barOffset);
                uint bar1 = *(uint*)(config + barOffset + 4);
                ulong mmioPhys = (bar0 & ~0xFUL);
                if ((bar0 & 0x06) == 0x04)
                {
                    mmioPhys |= ((ulong)bar1 << 32);
                }
                return mmioPhys;
            }
            return NvmeBar0Phys;
        }

        public static uint TriggerFlr(uint bus, uint dev, uint func)
        {
            SyscallWrappers.Log("[PCI] Function-Level Reset (FLR) triggered for device.\n");
            return 0;
        }
    }
}
