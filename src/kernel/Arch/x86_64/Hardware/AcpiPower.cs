using System;
using Kernel.Boot;
using Kernel.Diagnostics;
using Kernel.Memory.Virtual;

namespace Kernel.Arch.x86_64.Hardware
{
    public static unsafe class AcpiPower
    {
        private static volatile int s_spinSink;

        private static void Delay(int count)
        {
            for (int i = 0; i < count; i++)
            {
                s_spinSink = i;
            }
        }

        private static bool IsValidPhys(ulong phys)
        {
            return phys >= 0x1000UL && phys < (16UL * 1024 * 1024 * 1024);
        }

        private static void BlankScreen()
        {
            ulong fbPhys = KernelHigh.GopPhysBase;
            if (IsValidPhys(fbPhys))
            {
                uint* fb = (uint*)Hhdm.PhysicalToVirtual(fbPhys);
                ulong totalPixels = KernelHigh.GopFbSize > 0 ? (KernelHigh.GopFbSize / 4) : ((ulong)KernelHigh.GopWidth * KernelHigh.GopHeight);
                if (totalPixels > 0)
                {
                    ulong maxPixels = 3840UL * 2160UL;
                    if (totalPixels > maxPixels) totalPixels = maxPixels;
                    for (ulong i = 0; i < totalPixels; i++)
                    {
                        fb[i] = 0x00000000;
                    }
                }
            }
        }

        private static void WriteGas(byte* gas, ulong value)
        {
            byte space = gas[0];
            ulong addr = *(ulong*)(gas + 4);
            if (addr == 0) return;

            if (space == 1) // System I/O
            {
                ushort port = (ushort)addr;
                byte bitWidth = gas[1];
                if (bitWidth == 8)
                    PortIo.Out8(port, (byte)value);
                else if (bitWidth == 32)
                    PortIo.Out32(port, (uint)value);
                else
                    PortIo.Out16(port, (ushort)value);
            }
            else if (space == 0) // System Memory / MMIO
            {
                if (IsValidPhys(addr))
                {
                    ulong virt = Hhdm.PhysicalToVirtual(addr);
                    byte bitWidth = gas[1];
                    if (bitWidth == 8)
                        *(byte*)virt = (byte)value;
                    else if (bitWidth == 32)
                        *(uint*)virt = (uint)value;
                    else
                        *(ushort*)virt = (ushort)value;
                }
            }
        }

        private static ulong ReadGas(byte* gas)
        {
            byte space = gas[0];
            ulong addr = *(ulong*)(gas + 4);
            if (addr == 0) return 0;

            if (space == 1) // System I/O
            {
                ushort port = (ushort)addr;
                byte bitWidth = gas[1];
                if (bitWidth == 8) return PortIo.In8(port);
                if (bitWidth == 32) return PortIo.In32(port);
                return PortIo.In16(port);
            }
            else if (space == 0) // System Memory
            {
                if (IsValidPhys(addr))
                {
                    ulong virt = Hhdm.PhysicalToVirtual(addr);
                    byte bitWidth = gas[1];
                    if (bitWidth == 8) return *(byte*)virt;
                    if (bitWidth == 32) return *(uint*)virt;
                    return *(ushort*)virt;
                }
            }
            return 0;
        }

        private static byte* FindFadt(out byte* rootTableOut, out bool isXsdtOut, out int numEntriesOut)
        {
            rootTableOut = null;
            isXsdtOut = false;
            numEntriesOut = 0;

            ulong rsdpPhys = KernelHigh.RsdpPhysBase;
            if (!IsValidPhys(rsdpPhys)) return null;

            byte* rsdp = (byte*)Hhdm.PhysicalToVirtual(rsdpPhys);
            if (rsdp[0] != 'R' || rsdp[1] != 'S' || rsdp[2] != 'D' || rsdp[3] != ' ' ||
                rsdp[4] != 'P' || rsdp[5] != 'T' || rsdp[6] != 'R' || rsdp[7] != ' ')
            {
                return null;
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

            if (!IsValidPhys(rootTablePhys)) return null;

            byte* rootTable = (byte*)Hhdm.PhysicalToVirtual(rootTablePhys);
            bool isXsdt = (rootTable[0] == 'X' && rootTable[1] == 'S' && rootTable[2] == 'D' && rootTable[3] == 'T');
            bool isRsdt = (rootTable[0] == 'R' && rootTable[1] == 'S' && rootTable[2] == 'D' && rootTable[3] == 'T');
            if (!isXsdt && !isRsdt) return null;

            uint length = *(uint*)(rootTable + 4);
            if (length < 36 || length > 131072) return null;

            int entryStride = isXsdt ? 8 : 4;
            int numEntries = (int)((length - 36) / (uint)entryStride);
            if (numEntries > 256) numEntries = 256;

            rootTableOut = rootTable;
            isXsdtOut = isXsdt;
            numEntriesOut = numEntries;

            for (int i = 0; i < numEntries; i++)
            {
                ulong tablePhys = isXsdt ? *(ulong*)(rootTable + 36 + (i * 8)) : *(uint*)(rootTable + 36 + (i * 4));
                if (!IsValidPhys(tablePhys)) continue;

                byte* table = (byte*)Hhdm.PhysicalToVirtual(tablePhys);
                if (table[0] == 'F' && table[1] == 'A' && table[2] == 'C' && table[3] == 'P')
                {
                    return table;
                }
            }

            return null;
        }

        public static void Shutdown()
        {
            EarlySerial.WriteLine("[ACPI] Initiating system shutdown (S5 Soft Off)...");
            Cpu.DisableInterrupts();
            BlankScreen();

            byte* rootTable;
            bool isXsdt;
            int numEntries;
            byte* fadt = FindFadt(out rootTable, out isXsdt, out numEntries);

            if (fadt != null)
            {
                ExecuteFadtShutdown(fadt, rootTable, isXsdt, numEntries);
            }

            // Standard emulator poweroff ports fallback
            PortIo.Out16(0x604, 0x2000);
            PortIo.Out16(0x604, 0x0000);
            PortIo.Out16(0xB004, 0x2000);
            PortIo.Out16(0x4004, 0x3400);
            PortIo.Out8(0x3C0, 0x00);
            Delay(1000000);

            EarlySerial.WriteLine("[ACPI] Shutdown sequence complete. Halting CPU...");
            while (true)
            {
                Cpu.Halt();
            }
        }

        private static void ExecuteFadtShutdown(byte* fadt, byte* rootTable, bool isXsdt, int numEntries)
        {
            uint fadtLen = *(uint*)(fadt + 4);

            // Table 5-34: DSDT at offset 40 (4 bytes), X_DSDT at offset 140 (8 bytes)
            ulong dsdtPhys = 0;
            if (fadtLen >= 148)
            {
                ulong xdsdt = *(ulong*)(fadt + 140);
                if (IsValidPhys(xdsdt))
                {
                    dsdtPhys = xdsdt;
                }
            }
            if (dsdtPhys == 0 && fadtLen >= 44)
            {
                uint dsdt32 = *(uint*)(fadt + 40);
                if (IsValidPhys(dsdt32))
                {
                    dsdtPhys = dsdt32;
                }
            }

            uint smiCmd = (fadtLen >= 52) ? *(uint*)(fadt + 48) : 0;
            byte acpiEnable = (fadtLen >= 53) ? *(byte*)(fadt + 52) : (byte)0;
            uint pm1aPort = (fadtLen >= 68) ? *(uint*)(fadt + 64) : 0;
            uint pm1bPort = (fadtLen >= 72) ? *(uint*)(fadt + 68) : 0;

            byte* xPm1aGas = null;
            byte* xPm1bGas = null;

            // ACPI 2.0+ Extended PM1a/PM1b GAS:
            // Table 5-34: X_PM1a_CNT_BLK at offset 172 (12 bytes GAS)
            if (fadtLen >= 184)
            {
                byte* gas = fadt + 172;
                ulong addr = *(ulong*)(gas + 4);
                if (addr != 0)
                {
                    xPm1aGas = gas;
                    if (gas[0] == 1) pm1aPort = (uint)addr;
                }
            }

            // Table 5-34: X_PM1b_CNT_BLK at offset 184 (12 bytes GAS)
            if (fadtLen >= 196)
            {
                byte* gas = fadt + 184;
                ulong addr = *(ulong*)(gas + 4);
                if (addr != 0)
                {
                    xPm1bGas = gas;
                    if (gas[0] == 1) pm1bPort = (uint)addr;
                }
            }

            // ACPI 5.0+ Hardware-Reduced SLEEP_CONTROL_REG at offset 244 (12 bytes GAS)
            byte* sleepControlGas = null;
            if (fadtLen >= 256)
            {
                byte* gas = fadt + 244;
                ulong addr = *(ulong*)(gas + 4);
                if (addr != 0)
                {
                    sleepControlGas = gas;
                }
            }

            EarlySerial.Write("[ACPI] PM1a: 0x");
            EarlySerial.WriteHex(pm1aPort);
            EarlySerial.Write(" PM1b: 0x");
            EarlySerial.WriteHex(pm1bPort);
            if (sleepControlGas != null)
            {
                EarlySerial.Write(" SLEEP_CTRL: Present");
            }
            EarlySerial.WriteLine();

            // Enable ACPI if disabled (SCI_EN == 0)
            bool acpiActive = false;
            if (xPm1aGas != null)
            {
                acpiActive = (ReadGas(xPm1aGas) & 1) != 0;
            }
            else if (pm1aPort != 0)
            {
                acpiActive = (PortIo.In16((ushort)pm1aPort) & 1) != 0;
            }

            if (!acpiActive && smiCmd != 0 && acpiEnable != 0)
            {
                EarlySerial.WriteLine("[ACPI] Enabling ACPI mode via SMI_CMD...");
                PortIo.Out8((ushort)smiCmd, acpiEnable);
                for (int retry = 0; retry < 3000; retry++)
                {
                    if (xPm1aGas != null && (ReadGas(xPm1aGas) & 1) != 0) break;
                    if (pm1aPort != 0 && (PortIo.In16((ushort)pm1aPort) & 1) != 0) break;
                    PortIo.IoWait();
                }
            }

            // Parse DSDT or SSDTs for _S5_ sleep state
            byte slpTypA = 5;
            byte slpTypB = 5;
            bool foundS5 = false;

            if (dsdtPhys != 0 && IsValidPhys(dsdtPhys))
            {
                byte* dsdt = (byte*)Hhdm.PhysicalToVirtual(dsdtPhys);
                if (dsdt[0] == 'D' && dsdt[1] == 'S' && dsdt[2] == 'D' && dsdt[3] == 'T')
                {
                    uint dsdtLen = *(uint*)(dsdt + 4);
                    if (dsdtLen > 36 && dsdtLen < 4 * 1024 * 1024)
                    {
                        foundS5 = FindS5InAml(dsdt, dsdtLen, out slpTypA, out slpTypB);
                    }
                }
            }

            // Search SSDTs if _S5_ not present in DSDT
            if (!foundS5 && rootTable != null && numEntries > 0)
            {
                for (int i = 0; i < numEntries; i++)
                {
                    ulong tablePhys = isXsdt ? *(ulong*)(rootTable + 36 + (i * 8)) : *(uint*)(rootTable + 36 + (i * 4));
                    if (!IsValidPhys(tablePhys)) continue;

                    byte* table = (byte*)Hhdm.PhysicalToVirtual(tablePhys);
                    if (table[0] == 'S' && table[1] == 'S' && table[2] == 'D' && table[3] == 'T')
                    {
                        uint ssdtLen = *(uint*)(table + 4);
                        if (ssdtLen > 36 && ssdtLen < 4 * 1024 * 1024)
                        {
                            if (FindS5InAml(table, ssdtLen, out slpTypA, out slpTypB))
                            {
                                foundS5 = true;
                                EarlySerial.WriteLine("[ACPI] Found _S5_ sleep state in SSDT.");
                                break;
                            }
                        }
                    }
                }
            }

            EarlySerial.Write("[ACPI] S5 Sleep Types: A=");
            EarlySerial.WriteDec(slpTypA);
            EarlySerial.Write(" B=");
            EarlySerial.WriteDec(slpTypB);
            EarlySerial.WriteLine();

            // 1. ACPI 5.0+ Sleep Control Register
            if (sleepControlGas != null)
            {
                EarlySerial.WriteLine("[ACPI] Triggering SLEEP_CONTROL_REG...");
                byte slpCtrlVal = (byte)(((slpTypA & 7) << 2) | (1 << 5));
                WriteGas(sleepControlGas, slpCtrlVal);
                Delay(2000000);
            }

            // 2. Trigger S5 Soft Off via PM1a / PM1b CNT:
            // SLP_TYP in bits 10-12, SLP_EN in bit 13 (0x2000)
            ushort valA = (ushort)(((slpTypA & 7) << 10) | 0x2000);
            ushort valB = (ushort)(((slpTypB & 7) << 10) | 0x2000);

            if (xPm1aGas != null)
            {
                WriteGas(xPm1aGas, valA);
            }
            else if (pm1aPort != 0)
            {
                PortIo.Out16((ushort)pm1aPort, valA);
            }

            if (xPm1bGas != null)
            {
                WriteGas(xPm1bGas, valB);
            }
            else if (pm1bPort != 0)
            {
                PortIo.Out16((ushort)pm1bPort, valB);
            }

            // Wait for power supply sequencing
            Delay(4000000);

            // 3. Fallback sleep types (5, 7, 0, 3, 1)
            ushort* fallbackTypes = stackalloc ushort[5] { 5, 7, 0, 3, 1 };
            for (int t = 0; t < 5; t++)
            {
                ushort fbVal = (ushort)((fallbackTypes[t] << 10) | 0x2000);
                if (xPm1aGas != null) WriteGas(xPm1aGas, fbVal);
                else if (pm1aPort != 0) PortIo.Out16((ushort)pm1aPort, fbVal);

                if (xPm1bGas != null) WriteGas(xPm1bGas, fbVal);
                else if (pm1bPort != 0) PortIo.Out16((ushort)pm1bPort, fbVal);

                if (sleepControlGas != null)
                {
                    byte fbSlpCtrlVal = (byte)(((fallbackTypes[t] & 7) << 2) | (1 << 5));
                    WriteGas(sleepControlGas, fbSlpCtrlVal);
                }
                Delay(1000000);
            }
        }

        private static bool FindS5InAml(byte* table, uint len, out byte slpTypA, out byte slpTypB)
        {
            slpTypA = 5;
            slpTypB = 5;

            if (table == null || len < 40) return false;

            for (uint i = 36; i < len - 8; i++)
            {
                if (table[i] == '_' && table[i + 1] == 'S' && table[i + 2] == '5' && table[i + 3] == '_')
                {
                    uint pkgIdx = i + 4;
                    while (pkgIdx < len && pkgIdx < i + 12 && table[pkgIdx] != 0x12)
                    {
                        pkgIdx++;
                    }

                    if (pkgIdx < len && table[pkgIdx] == 0x12)
                    {
                        pkgIdx++; // Skip 0x12 (PackageOp)
                        byte lead = table[pkgIdx];
                        int numAdditional = (lead >> 6) & 3;
                        pkgIdx += (uint)(1 + numAdditional); // Skip PkgLength

                        if (pkgIdx < len)
                        {
                            byte numElements = table[pkgIdx++];
                            if (pkgIdx < len)
                            {
                                slpTypA = ReadAmlInteger(table, ref pkgIdx, len);
                            }
                            if (pkgIdx < len)
                            {
                                slpTypB = ReadAmlInteger(table, ref pkgIdx, len);
                            }
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static byte ReadAmlInteger(byte* aml, ref uint idx, uint maxLen)
        {
            if (idx >= maxLen) return 0;
            byte op = aml[idx++];
            if (op == 0x00) return 0;
            if (op == 0x01) return 1;
            if (op == 0xFF) return 0xFF;
            if (op == 0x0A && idx < maxLen)
            {
                return aml[idx++];
            }
            if (op == 0x0B && idx + 1 < maxLen)
            {
                byte val = aml[idx];
                idx += 2;
                return val;
            }
            if (op == 0x0C && idx + 3 < maxLen)
            {
                byte val = aml[idx];
                idx += 4;
                return val;
            }
            return op;
        }

        private static void ExecuteFadtReset()
        {
            byte* rootTable;
            bool isXsdt;
            int numEntries;
            byte* fadt = FindFadt(out rootTable, out isXsdt, out numEntries);
            if (fadt == null) return;

            uint fadtLen = *(uint*)(fadt + 4);
            // Table 5-34: RESET_REG at offset 116 (12 bytes GAS), RESET_VALUE at offset 128 (1 byte)
            if (fadtLen >= 129)
            {
                byte* resetGas = fadt + 116;
                ulong resetAddr = *(ulong*)(resetGas + 4);
                byte resetVal = *(byte*)(fadt + 128);
                if (resetAddr != 0)
                {
                    EarlySerial.WriteLine("[ACPI] Triggering FADT RESET_REG...");
                    WriteGas(resetGas, resetVal);
                    Delay(500000);
                }
            }
        }

        public static void Reboot()
        {
            EarlySerial.WriteLine("[RESET] Attempting hardware system reset...");
            Cpu.DisableInterrupts();

            // 0. ACPI FADT RESET_REG
            ExecuteFadtReset();

            // 1. PCI Reset Port 0xCF9 (standard on Intel & AMD chipsets)
            // 0x02 = System Reset, 0x06 = Reset CPU, 0x0E = Full Power Cycle
            PortIo.Out8(0xCF9, 0x02);
            Delay(200000);
            PortIo.Out8(0xCF9, 0x06);
            Delay(200000);
            PortIo.Out8(0xCF9, 0x0E);
            Delay(500000);

            // 2. i8042 Keyboard Controller pulse reset line
            PortIo.Out8(0x64, 0xFE);
            Delay(200000);

            // 3. Fast Reset Port 0x92
            byte val92 = PortIo.In8(0x92);
            PortIo.Out8(0x92, (byte)(val92 | 1));
            Delay(200000);

            // 4. Universal CPU Triple Fault Reset (guaranteed hardware reset)
            EarlySerial.WriteLine("[RESET] Triggering CPU Triple Fault reset...");
            Cpu.TripleFaultReset();

            // 5. Infinite Halt fallback
            while (true)
            {
                Cpu.Halt();
            }
        }
    }
}
