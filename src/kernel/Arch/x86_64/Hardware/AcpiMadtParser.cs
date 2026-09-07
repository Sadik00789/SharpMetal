using System;
using Kernel.Boot;
using Kernel.Diagnostics;
using Kernel.Memory.Virtual;

namespace Kernel.Arch.x86_64.Hardware
{
    public static unsafe class AcpiMadtParser
    {
        private static bool IsValidPhys(ulong phys)
        {
            return phys >= 0x1000UL && phys < (16UL * 1024 * 1024 * 1024);
        }

        public static bool Parse()
        {
            EarlySerial.WriteLine("[ACPI] Locating and parsing Multiple APIC Description Table (MADT)...");

            ulong rsdpPhys = KernelHigh.RsdpPhysBase;
            if (!IsValidPhys(rsdpPhys))
            {
                EarlySerial.WriteLine("[ACPI] Invalid RSDP physical base address.");
                return false;
            }

            byte* rsdp = (byte*)Hhdm.PhysicalToVirtual(rsdpPhys);
            if (rsdp[0] != 'R' || rsdp[1] != 'S' || rsdp[2] != 'D' || rsdp[3] != ' ' ||
                rsdp[4] != 'P' || rsdp[5] != 'T' || rsdp[6] != 'R' || rsdp[7] != ' ')
            {
                EarlySerial.WriteLine("[ACPI] Invalid RSDP signature.");
                return false;
            }

            byte revision = rsdp[15];
            ulong rootTablePhys = 0;
            if (revision >= 2)
            {
                ulong xsdt = *(ulong*)(rsdp + 24);
                if (IsValidPhys(xsdt)) rootTablePhys = xsdt;
            }
            if (rootTablePhys == 0)
            {
                uint rsdt = *(uint*)(rsdp + 16);
                if (IsValidPhys(rsdt)) rootTablePhys = rsdt;
            }

            if (!IsValidPhys(rootTablePhys))
            {
                EarlySerial.WriteLine("[ACPI] Could not locate RSDT/XSDT table.");
                return false;
            }

            byte* rootTable = (byte*)Hhdm.PhysicalToVirtual(rootTablePhys);
            bool isXsdt = (rootTable[0] == 'X' && rootTable[1] == 'S' && rootTable[2] == 'D' && rootTable[3] == 'T');
            bool isRsdt = (rootTable[0] == 'R' && rootTable[1] == 'S' && rootTable[2] == 'D' && rootTable[3] == 'T');
            if (!isXsdt && !isRsdt)
            {
                EarlySerial.WriteLine("[ACPI] Root table signature is neither RSDT nor XSDT.");
                return false;
            }

            uint rootLength = *(uint*)(rootTable + 4);
            if (rootLength < 36 || rootLength > 131072) return false;

            int entryStride = isXsdt ? 8 : 4;
            int numEntries = (int)((rootLength - 36) / (uint)entryStride);
            if (numEntries > 256) numEntries = 256;

            byte* madt = null;
            for (int i = 0; i < numEntries; i++)
            {
                ulong tablePhys = isXsdt ? *(ulong*)(rootTable + 36 + (i * 8)) : *(uint*)(rootTable + 36 + (i * 4));
                if (!IsValidPhys(tablePhys)) continue;

                byte* table = (byte*)Hhdm.PhysicalToVirtual(tablePhys);
                if (table[0] == 'A' && table[1] == 'P' && table[2] == 'I' && table[3] == 'C')
                {
                    madt = table;
                    break;
                }
            }

            if (madt == null)
            {
                EarlySerial.WriteLine("[ACPI] MADT ('APIC') table not found!");
                return false;
            }

            uint madtLength = *(uint*)(madt + 4);
            uint localApicAddress = *(uint*)(madt + 36);
            uint madtFlags = *(uint*)(madt + 40);

            EarlySerial.Write("[ACPI] Found MADT at length: ");
            EarlySerial.WriteDec(madtLength);
            EarlySerial.Write(" LAPIC Address: 0x");
            EarlySerial.WriteHex(localApicAddress);
            EarlySerial.WriteLine("");

            // Traverse Interrupt Controller Structures starting at offset 44
            uint offset = 44;
            while (offset + 2 <= madtLength)
            {
                byte type = madt[offset];
                byte length = madt[offset + 1];
                if (length < 2) break; // Corrupt record guard

                if (type == 0 && length >= 8) // Processor Local APIC
                {
                    byte processorUid = madt[offset + 2];
                    byte apicId = madt[offset + 3];
                    uint flags = *(uint*)(madt + offset + 4);

                    // Check Flags: Bit 0 = Enabled, Bit 1 = Online Capable
                    bool enabled = (flags & 1) != 0;
                    bool onlineCapable = (flags & 2) != 0;

                    if (enabled || onlineCapable)
                    {
                        CpuTopology.RegisterCore(apicId, processorUid, flags);
                        EarlySerial.Write("[ACPI] Discovered Core ");
                        EarlySerial.WriteDec(CpuTopology.CoreCount - 1);
                        EarlySerial.Write(" - APIC ID: ");
                        EarlySerial.WriteDec(apicId);
                        EarlySerial.Write(", UID: ");
                        EarlySerial.WriteDec(processorUid);
                        EarlySerial.Write(enabled ? " [Enabled]" : " [OnlineCapable]");
                        EarlySerial.WriteLine("");
                    }
                }

                offset += length;
            }

            EarlySerial.Write("[ACPI] MADT enumeration complete. Total cores discovered: ");
            EarlySerial.WriteDec(CpuTopology.CoreCount);
            EarlySerial.WriteLine("");

            return CpuTopology.CoreCount > 0;
        }
    }
}
