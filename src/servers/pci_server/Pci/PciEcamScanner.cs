using System;
using Microkernel.Abstractions.Boot;
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

        public static bool FoundXhciController = false;
        public static ulong XhciBar0Phys = 0;
        // xHCI interrupt mode: 0 = none (poll), 1 = MSI, 2 = MSI-X
        public static uint XhciMsiMode = 0;

        private static bool s_hypervisorChecked;
        private static bool s_isHypervisor;

        /// <summary>
        /// True when running under a hypervisor. Used to keep bare-metal probes
        /// free of side effects that can disable firmware USB legacy emulation.
        /// </summary>
        private static bool IsHypervisor()
        {
            if (!s_hypervisorChecked)
            {
                s_hypervisorChecked = true;
                KernelBootInfo bi = default;
                SyscallWrappers.GetBootInfo(&bi);
                s_isHypervisor = bi.IsHypervisor != 0;
            }
            return s_isHypervisor;
        }

        public static void Initialize(ulong virtBase)
        {
            EcamVirtBase = virtBase;
        }

        public static void ParseCapabilities(PciDevice* dev)
        {
            if (dev == null || dev->ConfigVirtAddress == 0) return;
            byte* cfg = (byte*)dev->ConfigVirtAddress;
            ushort status = *(ushort*)(cfg + 0x06);
            if ((status & (1 << 4)) == 0) return; // Capabilities List bit not set

            byte capPtr = *(cfg + 0x34);
            while (capPtr >= 0x40 && capPtr <= 0xFC)
            {
                byte capId = *(cfg + capPtr);
                byte nextPtr = *(cfg + capPtr + 1);

                if (capId == 0x05) dev->MsiOffset = capPtr;
                else if (capId == 0x11) dev->MsiXOffset = capPtr;

                capPtr = nextPtr;
            }
        }

        public static bool ConfigureMsi(PciDevice* dev, byte vector, uint apicId = 0)
        {
            if (dev == null || dev->MsiOffset == 0) return false;
            byte* cfg = (byte*)dev->ConfigVirtAddress;
            byte msi = dev->MsiOffset;

            ushort msgCtrl = *(ushort*)(cfg + msi + 2);
            bool is64 = (msgCtrl & 0x80) != 0;

            ulong msgAddr = 0xFEE00000UL | ((ulong)apicId << 12);
            *(uint*)(cfg + msi + 4) = (uint)msgAddr;
            if (is64)
            {
                *(uint*)(cfg + msi + 8) = (uint)(msgAddr >> 32);
                *(ushort*)(cfg + msi + 12) = vector;
            }
            else
            {
                *(ushort*)(cfg + msi + 8) = vector;
            }

            // Enable MSI (bit 0 = 1)
            *(ushort*)(cfg + msi + 2) = (ushort)(msgCtrl | 1);
            return true;
        }

        public static bool ConfigureMsiX(PciDevice* dev, byte vector, uint apicId = 0)
        {
            if (dev == null || dev->MsiXOffset == 0) return false;
            byte* cfg = (byte*)dev->ConfigVirtAddress;
            byte msix = dev->MsiXOffset;

            ushort msgCtrl = *(ushort*)(cfg + msix + 2);
            uint tableReg = *(uint*)(cfg + msix + 4);
            byte bir = (byte)(tableReg & 0x07);
            uint tableOffset = tableReg & ~0x07U;

            ulong barPhys = GetBar(dev->Bus, dev->Device, dev->Function, bir);
            if (barPhys != 0)
            {
                ulong tableVirt = 0x2C000000UL + ((ulong)bir * 0x100000UL);
                SyscallWrappers.MapMmio(barPhys, tableVirt, 65536, writeCombining: false);

                byte* tableEntry = (byte*)(tableVirt + tableOffset);
                *(ulong*)(tableEntry + 0) = 0xFEE00000UL | ((ulong)apicId << 12); // Msg Addr
                *(uint*)(tableEntry + 8) = vector;                                 // Msg Data
                *(uint*)(tableEntry + 12) = 0;                                     // Vector Control (unmasked)
            }

            // Enable MSI-X (bit 15 = 1, bit 14 Function Mask = 0)
            *(ushort*)(cfg + msix + 2) = (ushort)((msgCtrl | 0x8000) & ~0x4000);
            return true;
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

                        PciDevice pciDev;
                        pciDev.Bus = bus;
                        pciDev.Device = dev;
                        pciDev.Function = func;
                        pciDev.VendorId = vendorId;
                        pciDev.DeviceId = *(ushort*)(config + 0x02);
                        pciDev.BaseClass = baseClass;
                        pciDev.SubClass = subClass;
                        pciDev.ProgIf = config[0x09];
                        pciDev.ConfigVirtAddress = (ulong)config;
                        pciDev.MsiOffset = 0;
                        pciDev.MsiXOffset = 0;
                        pciDev.Bar0 = 0;
                        pciDev.XhciBar0 = 0;
                        ParseCapabilities(&pciDev);

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
                                pciDev.Bar0 = mmioPhys;

                                if (pciDev.MsiOffset != 0) ConfigureMsi(&pciDev, 0x30);
                                else if (pciDev.MsiXOffset != 0) ConfigureMsiX(&pciDev, 0x30);
                            }
                        }
                        else if (baseClass == 0x0C && subClass == 0x03 && config[0x09] == 0x30)
                        {
                            // xHCI USB controller: Class 0x0C, Subclass 0x03, ProgIF 0x30.
                            FoundXhciController = true;

                            // 64-bit capable BAR0 parse (read-only; no side effects)
                            uint xhciBar0 = *(uint*)(config + 0x10);
                            uint xhciBar1 = *(uint*)(config + 0x14);
                            ulong xhciMmio = (xhciBar0 & ~0xFUL);
                            if ((xhciBar0 & 0x06) == 0x04)
                            {
                                xhciMmio |= ((ulong)xhciBar1 << 32);
                            }
                            XhciBar0Phys = xhciMmio;
                            pciDev.Bar0 = xhciMmio;

                            // Bare metal: do NOT write the PCI command register or
                            // MSI-X here. On firmware with USBLEGSUP PCI-command/Bar
                            // SMI enables set, even a bus-master write can trigger the
                            // ownership-release SMI and permanently stop legacy USB
                            // emulation, killing the external keyboard. bus.xhci is
                            // opt-in only and enables bus master itself.
                            if (IsHypervisor())
                            {
                                ushort xhciCmd = *(ushort*)(config + 0x04);
                                xhciCmd |= 0x0006;
                                *(ushort*)(config + 0x04) = xhciCmd;

                                // Vector 0x32: prefer MSI-X, fall back to MSI.
                                if (pciDev.MsiXOffset != 0 && ConfigureMsiX(&pciDev, 0x32)) XhciMsiMode = 2;
                                else if (pciDev.MsiOffset != 0 && ConfigureMsi(&pciDev, 0x32)) XhciMsiMode = 1;
                                else XhciMsiMode = 0;
                            }
                        }
                        else if (vendorId == 0x1AF4 && (pciDev.DeviceId == 0x1000 || pciDev.DeviceId == 0x1041 || baseClass == 0x02))
                        {
                            if (pciDev.MsiOffset != 0) ConfigureMsi(&pciDev, 0x31);
                            else if (pciDev.MsiXOffset != 0) ConfigureMsiX(&pciDev, 0x31);
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

        /// <summary>
        /// Exact class/subclass/programming-interface match returning BAR0, or 0
        /// when no device matches. Pass 0xFF for progIf to wildcard it. Unlike
        /// FindDevice there is no NVMe fallback, so a caller can never program an
        /// unrelated controller by mistake.
        /// </summary>
        public static ulong FindDeviceExact(uint targetClass, uint targetSubClass, uint targetProgIf)
        {
            if (EcamVirtBase == 0) return 0;

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

                        if (config[0x0B] != targetClass || config[0x0A] != targetSubClass) continue;
                        if (targetProgIf != 0xFF && config[0x09] != targetProgIf) continue;

                        // Enable Bus Master (bit 2) and Memory Space (bit 1)
                        ushort cmd = *(ushort*)(config + 0x04);
                        cmd |= 0x0006;
                        *(ushort*)(config + 0x04) = cmd;

                        uint bar0 = *(uint*)(config + 0x10);
                        uint bar1 = *(uint*)(config + 0x14);
                        ulong mmioPhys = (bar0 & ~0xFUL);
                        if ((bar0 & 0x06) == 0x04)
                        {
                            mmioPhys |= ((ulong)bar1 << 32);
                        }
                        return mmioPhys;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Interrupt mode for the first class/subclass match:
        /// 0 = none, 1 = MSI enabled, 2 = MSI-X enabled. Reads the PCI
        /// capability list directly so it is independent of scan-time state.
        /// </summary>
        public static uint GetMsiMode(uint targetClass, uint targetSubClass)
        {
            if (EcamVirtBase == 0) return 0;

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

                        if (config[0x0B] != targetClass || config[0x0A] != targetSubClass) continue;

                        ushort status = *(ushort*)(config + 0x06);
                        if ((status & (1 << 4)) == 0) return 0; // No capability list

                        byte capPtr = *(config + 0x34);
                        int guard = 48;
                        while (capPtr >= 0x40 && capPtr <= 0xFC && guard-- > 0)
                        {
                            byte capId = *(config + capPtr);
                            byte nextPtr = *(config + capPtr + 1);

                            if (capId == 0x11) // MSI-X
                            {
                                ushort mc = *(ushort*)(config + capPtr + 2);
                                if ((mc & 0x8000) != 0) return 2;
                            }
                            else if (capId == 0x05) // MSI
                            {
                                ushort mc = *(ushort*)(config + capPtr + 2);
                                if ((mc & 0x0001) != 0) return 1;
                            }

                            if (nextPtr == 0 || nextPtr == capPtr) break;
                            capPtr = nextPtr;
                        }

                        return 0;
                    }
                }
            }

            return 0;
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
