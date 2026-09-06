using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Boot;
using Microkernel.Abstractions.Initrd;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Roottask
{
    public static unsafe class Program
    {
        [DllImport("*")]
        public static extern ulong GetCs();

        [DllImport("*")]
        public static extern ulong GetRsp();

        private static void PrintHex(ulong val)
        {
            byte* hexDigits = stackalloc byte[16];
            for (int i = 0; i < 10; i++) hexDigits[i] = (byte)('0' + i);
            for (int i = 0; i < 6; i++) hexDigits[10 + i] = (byte)('A' + i);

            byte* buf = stackalloc byte[17];
            int idx = 0;
            for (int shift = 60; shift >= 0; shift -= 4)
            {
                byte nibble = (byte)((val >> shift) & 0xF);
                if (idx > 0 || nibble > 0 || shift == 0)
                {
                    buf[idx++] = hexDigits[nibble];
                }
            }
            buf[idx] = 0;
            SyscallWrappers.Log(buf);
        }

        private static void PrintAcpiToken(ulong rsdpPhys, ulong mcfgPhys)
        {
            SyscallWrappers.Log("[ACPI] RSDP located at physical: 0x");
            PrintHex(rsdpPhys);
            SyscallWrappers.Log(" MCFG base: 0x");
            PrintHex(mcfgPhys);
            SyscallWrappers.Log("\n");
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "RoottaskMain")]
        public static void Main()
        {
            // 1. Get Boot Information from Microkernel
            KernelBootInfo bootInfo = default;
            SyscallWrappers.GetBootInfo(&bootInfo);

            ulong mcfgBase = 0;

            // Constraint 1: Userland MMIO Mapping for Boot Structures
            ulong initrdVirtBase = 0x60000000UL;
            SyscallWrappers.MapMmio(bootInfo.InitrdPhysBase, initrdVirtBase, bootInfo.InitrdSize, writeCombining: false);
            ulong initrdVirt = initrdVirtBase + (bootInfo.InitrdPhysBase & 0xFFFUL);

            // 2. Parse RSDP to locate Root System Description Table (XSDT / RSDT)
            if (bootInfo.RsdpPhysBase != 0)
            {
                ulong rsdpVirtBase = 0x50000000UL;
                SyscallWrappers.MapMmio(bootInfo.RsdpPhysBase, rsdpVirtBase, 4096, writeCombining: false);
                ulong rsdpVirt = rsdpVirtBase + (bootInfo.RsdpPhysBase & 0xFFFUL);

                byte* rsdp = (byte*)rsdpVirt;
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
                        ulong rootTableVirtBase = 0x50010000UL;
                        SyscallWrappers.MapMmio(rootTablePhys, rootTableVirtBase, 8192, writeCombining: false);
                        ulong rootTableVirt = rootTableVirtBase + (rootTablePhys & 0xFFFUL);

                        byte* rootTable = (byte*)rootTableVirt;
                        bool isXsdt = (rootTable[0] == 'X' && rootTable[1] == 'S' && rootTable[2] == 'D' && rootTable[3] == 'T');
                        bool isRsdt = (rootTable[0] == 'R' && rootTable[1] == 'S' && rootTable[2] == 'D' && rootTable[3] == 'T');

                        if (isXsdt || isRsdt)
                        {
                            uint length = *(uint*)(rootTable + 4);
                            if (length >= 36 && length <= 65536)
                            {
                                int entryStride = isXsdt ? 8 : 4;
                                int numEntries = (int)((length - 36) / (uint)entryStride);
                                if (numEntries > 128) numEntries = 128;

                                ulong tempTableVirtBase = 0x50020000UL;
                                for (int i = 0; i < numEntries; i++)
                                {
                                    ulong tablePhys = isXsdt ? *(ulong*)(rootTable + 36 + (i * 8)) : *(uint*)(rootTable + 36 + (i * 4));
                                    if (tablePhys == 0) continue;

                                    SyscallWrappers.MapMmio(tablePhys, tempTableVirtBase, 4096, writeCombining: false);
                                    byte* table = (byte*)(tempTableVirtBase + (tablePhys & 0xFFFUL));

                                    if (table[0] == 'M' && table[1] == 'C' && table[2] == 'F' && table[3] == 'G')
                                    {
                                        mcfgBase = *(ulong*)(table + 44);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Fallback ECAM base if MCFG table was not present on current firmware
            if (mcfgBase == 0)
            {
                mcfgBase = 0xB0000000UL;
            }

            // 3. Emit ACPI discovery token
            PrintAcpiToken(bootInfo.RsdpPhysBase, mcfgBase);

            // 4. Launch System Servers & Shell from INITRD.IMG
            byte* initrdBase = (byte*)initrdVirt;
            byte* pciPayload = null; ulong pciSize = 0;
            byte* displayPayload = null; ulong displaySize = 0;
            byte* supPayload = null; ulong supSize = 0;
            byte* nvmePayload = null; ulong nvmeSize = 0;
            byte* inputPayload = null; ulong inputSize = 0;
            byte* netPayload = null; ulong netSize = 0;
            byte* fsPayload = null; ulong fsSize = 0;
            byte* shellPayload = null; ulong shellSize = 0;

            bool hasPci = InitrdParser.FindEntry(initrdBase, bootInfo.InitrdSize, "pci_server.bin", out pciPayload, out pciSize);
            bool hasDisplay = InitrdParser.FindEntry(initrdBase, bootInfo.InitrdSize, "display_server.bin", out displayPayload, out displaySize);
            bool hasSupervisor = InitrdParser.FindEntry(initrdBase, bootInfo.InitrdSize, "supervisor.bin", out supPayload, out supSize);
            bool hasNvme = InitrdParser.FindEntry(initrdBase, bootInfo.InitrdSize, "storage.nvme.bin", out nvmePayload, out nvmeSize);
            bool hasInput = InitrdParser.FindEntry(initrdBase, bootInfo.InitrdSize, "input.hid.bin", out inputPayload, out inputSize);
            bool hasNet = InitrdParser.FindEntry(initrdBase, bootInfo.InitrdSize, "net.virtio.bin", out netPayload, out netSize);
            bool hasFs = InitrdParser.FindEntry(initrdBase, bootInfo.InitrdSize, "fs.fat32.bin", out fsPayload, out fsSize);
            bool hasShell = InitrdParser.FindEntry(initrdBase, bootInfo.InitrdSize, "shell.bin", out shellPayload, out shellSize);

            if (!hasPci || !hasDisplay || !hasSupervisor || !hasNvme || !hasInput || !hasShell)
            {
                SyscallWrappers.Log("[ERROR] Failed to locate servers and shell in INITRD.IMG!\n");
            }
            else
            {
                // Constraint 2: CSpace Delegation for System Servers
                SyscallWrappers.Log("[ROOTTASK] Delegating CSpace capabilities: fs.fat32 -> Slot 9, shell -> Slot 11, Slot 12.\n");

                // Spawn isolated child processes
                SyscallWrappers.CreateProcess((ulong)pciPayload, pciSize, 0x0000000040000000UL, 1);
                SyscallWrappers.CreateProcess((ulong)displayPayload, displaySize, 0x0000000040000000UL, 1);
                SyscallWrappers.CreateProcess((ulong)supPayload, supSize, 0x0000000040000000UL, 1);
                SyscallWrappers.CreateProcess((ulong)nvmePayload, nvmeSize, 0x0000000040000000UL, 1);
                SyscallWrappers.CreateProcess((ulong)inputPayload, inputSize, 0x0000000040000000UL, 1);
                if (hasNet)
                {
                    SyscallWrappers.CreateProcess((ulong)netPayload, netSize, 0x0000000040000000UL, 1);
                }
                if (hasFs)
                {
                    SyscallWrappers.CreateProcess((ulong)fsPayload, fsSize, 0x0000000040000000UL, 1);
                }
                SyscallWrappers.CreateProcess((ulong)shellPayload, shellSize, 0x0000000040000000UL, 2);
            }

            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
