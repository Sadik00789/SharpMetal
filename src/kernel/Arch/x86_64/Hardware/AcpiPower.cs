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

        public static void Shutdown()
        {
            EarlySerial.WriteLine("[ACPI] Initiating system shutdown (S5 Soft Off)...");

            ulong rsdpPhys = KernelHigh.RsdpPhysBase;
            if (rsdpPhys != 0)
            {
                byte* rsdp = (byte*)Hhdm.PhysicalToVirtual(rsdpPhys);
                if (rsdp[0] == 'R' && rsdp[1] == 'S' && rsdp[2] == 'D' && rsdp[3] == ' ' &&
                    rsdp[4] == 'P' && rsdp[5] == 'T' && rsdp[6] == 'R' && rsdp[7] == ' ')
                {
                    byte revision = rsdp[15];
                    ulong rootTablePhys = 0;
                    if (revision >= 2)
                    {
                        rootTablePhys = *(ulong*)(rsdp + 24); // XsdtAddress
                    }
                    if (rootTablePhys == 0)
                    {
                        rootTablePhys = *(uint*)(rsdp + 16);  // RsdtAddress
                    }

                    if (rootTablePhys != 0)
                    {
                        byte* rootTable = (byte*)Hhdm.PhysicalToVirtual(rootTablePhys);
                        bool isXsdt = (rootTable[0] == 'X' && rootTable[1] == 'S' && rootTable[2] == 'D' && rootTable[3] == 'T');
                        bool isRsdt = (rootTable[0] == 'R' && rootTable[1] == 'S' && rootTable[2] == 'D' && rootTable[3] == 'T');

                        if (isXsdt || isRsdt)
                        {
                            uint length = *(uint*)(rootTable + 4);
                            if (length >= 36 && length <= 131072)
                            {
                                int entryStride = isXsdt ? 8 : 4;
                                int numEntries = (int)((length - 36) / (uint)entryStride);
                                if (numEntries > 256) numEntries = 256;

                                byte* fadt = null;
                                for (int i = 0; i < numEntries; i++)
                                {
                                    ulong tablePhys = isXsdt ? *(ulong*)(rootTable + 36 + (i * 8)) : *(uint*)(rootTable + 36 + (i * 4));
                                    if (tablePhys == 0) continue;

                                    byte* table = (byte*)Hhdm.PhysicalToVirtual(tablePhys);
                                    if (table[0] == 'F' && table[1] == 'A' && table[2] == 'C' && table[3] == 'P')
                                    {
                                        fadt = table;
                                        break;
                                    }
                                }

                                if (fadt != null)
                                {
                                    ExecuteFadtShutdown(fadt);
                                }
                            }
                        }
                    }
                }
            }

            // If ACPI S5 Soft Off didn't power off the system, run hardware reset fallbacks
            Reboot();
        }

        private static void ExecuteFadtShutdown(byte* fadt)
        {
            uint fadtLen = *(uint*)(fadt + 4);

            ulong dsdtPhys = 0;
            if (fadtLen >= 148)
            {
                dsdtPhys = *(ulong*)(fadt + 112); // X_DSDT
            }
            if (dsdtPhys == 0)
            {
                dsdtPhys = *(uint*)(fadt + 40);  // DSDT
            }

            uint smiCmd = *(uint*)(fadt + 48);
            byte acpiEnable = *(byte*)(fadt + 52);
            uint pm1aCnt = *(uint*)(fadt + 64);
            uint pm1bCnt = *(uint*)(fadt + 68);

            // Check ACPI 2.0+ Extended PM1a/PM1b GAS
            if (fadtLen >= 152)
            {
                byte pm1aGasSpace = *(byte*)(fadt + 140);
                ulong pm1aGasAddr = *(ulong*)(fadt + 140 + 4);
                if (pm1aGasSpace == 1 && pm1aGasAddr != 0)
                {
                    pm1aCnt = (uint)pm1aGasAddr;
                }

                byte pm1bGasSpace = *(byte*)(fadt + 152);
                ulong pm1bGasAddr = *(ulong*)(fadt + 152 + 4);
                if (pm1bGasSpace == 1 && pm1bGasAddr != 0)
                {
                    pm1bCnt = (uint)pm1bGasAddr;
                }
            }

            EarlySerial.Write("[ACPI] PM1a_CNT: 0x");
            EarlySerial.WriteHex(pm1aCnt);
            EarlySerial.Write(" PM1b_CNT: 0x");
            EarlySerial.WriteHex(pm1bCnt);
            EarlySerial.WriteLine();

            // Enable ACPI if disabled (SCI_EN == 0)
            if (pm1aCnt != 0 && (PortIo.In16((ushort)pm1aCnt) & 1) == 0)
            {
                if (smiCmd != 0 && acpiEnable != 0)
                {
                    EarlySerial.WriteLine("[ACPI] Enabling ACPI mode via SMI_CMD...");
                    PortIo.Out8((ushort)smiCmd, acpiEnable);
                    for (int retry = 0; retry < 3000; retry++)
                    {
                        if ((PortIo.In16((ushort)pm1aCnt) & 1) != 0)
                            break;
                        PortIo.IoWait();
                    }
                }
            }

            // Parse DSDT for _S5_ sleep state
            byte slpTypA = 5;
            byte slpTypB = 5;

            if (dsdtPhys != 0)
            {
                byte* dsdt = (byte*)Hhdm.PhysicalToVirtual(dsdtPhys);
                if (dsdt[0] == 'D' && dsdt[1] == 'S' && dsdt[2] == 'D' && dsdt[3] == 'T')
                {
                    uint dsdtLen = *(uint*)(dsdt + 4);
                    if (dsdtLen > 36 && dsdtLen < 2 * 1024 * 1024)
                    {
                        for (uint i = 36; i < dsdtLen - 8; i++)
                        {
                            if (dsdt[i] == '_' && dsdt[i + 1] == 'S' && dsdt[i + 2] == '5' && dsdt[i + 3] == '_')
                            {
                                uint pkgIdx = i + 4;
                                while (pkgIdx < dsdtLen && pkgIdx < i + 12 && dsdt[pkgIdx] != 0x12)
                                {
                                    pkgIdx++;
                                }

                                if (pkgIdx < dsdtLen && dsdt[pkgIdx] == 0x12)
                                {
                                    pkgIdx++; // Skip 0x12 (PackageOp)
                                    byte lead = dsdt[pkgIdx];
                                    int numAdditional = (lead >> 6) & 3;
                                    pkgIdx += (uint)(1 + numAdditional); // Skip PkgLength

                                    if (pkgIdx < dsdtLen)
                                    {
                                        byte numElements = dsdt[pkgIdx++];
                                        if (pkgIdx < dsdtLen)
                                        {
                                            slpTypA = ReadAmlInteger(dsdt, ref pkgIdx, dsdtLen);
                                        }
                                        if (pkgIdx < dsdtLen)
                                        {
                                            slpTypB = ReadAmlInteger(dsdt, ref pkgIdx, dsdtLen);
                                        }
                                        EarlySerial.Write("[ACPI] Found _S5_ sleep types: A=");
                                        EarlySerial.WriteDec(slpTypA);
                                        EarlySerial.Write(" B=");
                                        EarlySerial.WriteDec(slpTypB);
                                        EarlySerial.WriteLine();
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Trigger S5 Soft Off: SLP_TYP in bits 10-12, SLP_EN in bit 13 (0x2000)
            if (pm1aCnt != 0)
            {
                ushort valA = (ushort)(((slpTypA & 7) << 10) | 0x2000);
                PortIo.Out16((ushort)pm1aCnt, valA);

                if (pm1bCnt != 0)
                {
                    ushort valB = (ushort)(((slpTypB & 7) << 10) | 0x2000);
                    PortIo.Out16((ushort)pm1bCnt, valB);
                }

                // Wait for power supply sequencing
                Delay(5000000);

                // Try common fallback sleep types if not yet off
                ushort* fallbackTypes = stackalloc ushort[4] { 5, 7, 0, 3 };
                for (int t = 0; t < 4; t++)
                {
                    ushort fbVal = (ushort)((fallbackTypes[t] << 10) | 0x2000);
                    PortIo.Out16((ushort)pm1aCnt, fbVal);
                    if (pm1bCnt != 0) PortIo.Out16((ushort)pm1bCnt, fbVal);
                    Delay(2000000);
                }
            }

            // Check FADT RESET_REG
            if (fadtLen >= 129)
            {
                byte resetSpace = *(byte*)(fadt + 116);
                ulong resetAddr = *(ulong*)(fadt + 116 + 4);
                byte resetVal = *(byte*)(fadt + 128);
                if (resetSpace == 1 && resetAddr != 0)
                {
                    EarlySerial.WriteLine("[ACPI] Triggering FADT RESET_REG...");
                    PortIo.Out8((ushort)resetAddr, resetVal);
                    Delay(2000000);
                }
            }
        }

        private static byte ReadAmlInteger(byte* dsdt, ref uint idx, uint maxLen)
        {
            if (idx >= maxLen) return 0;
            byte op = dsdt[idx++];
            if (op == 0x00) return 0;
            if (op == 0x01) return 1;
            if (op == 0xFF) return 0xFF;
            if (op == 0x0A && idx < maxLen)
            {
                return dsdt[idx++];
            }
            if (op == 0x0B && idx + 1 < maxLen)
            {
                byte val = dsdt[idx];
                idx += 2;
                return val;
            }
            if (op == 0x0C && idx + 3 < maxLen)
            {
                byte val = dsdt[idx];
                idx += 4;
                return val;
            }
            return op;
        }

        public static void Reboot()
        {
            EarlySerial.WriteLine("[RESET] Attempting hardware system reset...");

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

            // 4. Halt CPU forever
            Cpu.Halt();
        }
    }
}
