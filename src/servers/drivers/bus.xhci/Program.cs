using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Xhci
{
    public static unsafe class Program
    {
        // Enumeration retry and hot-plug polling cadence, in loop iterations
        // (each iteration yields), approximating a ~500 ms period.
        private const uint RetryCadence = 200000;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "XhciMain")]
        public static void Main()
        {
            // Let the PCI server and input.hid finish bringing their services
            // online before touching PCI config space or calling InjectKey.
            for (int i = 0; i < 8; i++)
            {
                SyscallWrappers.Yield();
            }

            // 1. PCIe ECAM scan across buses 0..3
            ulong ecamVirt = 0x20000000UL;
            SyscallWrappers.MapMmio(0xE0000000UL, ecamVirt, 4194304, writeCombining: false);

            bool found = false;
            ulong bar0Phys = 0;

            for (uint bus = 0; bus < 4; bus++)
            {
                for (uint dev = 0; dev < 32; dev++)
                {
                    for (uint func = 0; func < 8; func++)
                    {
                        ulong offset = (bus << 20) | (dev << 15) | (func << 12);
                        byte* config = (byte*)(ecamVirt + offset);
                        ushort vendorId = *(ushort*)(config + 0x00);
                        if (vendorId == 0xFFFF || vendorId == 0x0000)
                        {
                            if (func == 0) break;
                            continue;
                        }

                        byte baseClass = config[0x0B];
                        byte subClass = config[0x0A];
                        byte progIf = config[0x09];

                        if (baseClass == 0x0C && subClass == 0x03 && progIf == 0x30)
                        {
                            found = true;

                            // Enable Bus Master (bit 2) and Memory Space (bit 1)
                            ushort cmd = *(ushort*)(config + 0x04);
                            cmd |= 0x0006;
                            *(ushort*)(config + 0x04) = cmd;

                            uint b0 = *(uint*)(config + 0x10);
                            uint b1 = *(uint*)(config + 0x14);
                            bar0Phys = (b0 & ~0xFUL);
                            if ((b0 & 0x06) == 0x04)
                            {
                                bar0Phys |= ((ulong)b1 << 32);
                            }
                            break;
                        }
                    }
                    if (found) break;
                }
                if (found) break;
            }

            if (!found)
            {
                var pci = new Microkernel.Abstractions.Services.PciServiceClient(endpointCptr: 6);
                ulong pciBar = pci.FindDeviceExact(0x0C, 0x03, 0x30);
                if (pciBar != 0)
                {
                    found = true;
                    bar0Phys = pciBar;
                }
            }

            if (!found || bar0Phys == 0)
            {
                SyscallWrappers.Log("[XHCI] no xHC found on PCI\n");
                return;
            }

            SyscallWrappers.MapMmio(bar0Phys, XhciController.Bar0Virt, XhciController.Bar0MapSize, writeCombining: false);
            XhciController.Bar0 = (byte*)XhciController.Bar0Virt;

            bool initialized = XhciController.Initialize();

            // MSI-X vector 0x32 stays registered in the kernel InterruptDispatcher
            // (Notification Slot 15) so events are routed and EOI'd on the kernel
            // path. The driver nevertheless drains the Event Ring unconditionally
            // rather than blocking in RecvAny: input must never depend on
            // interrupt delivery succeeding, which is the guarantee that keeps
            // the keyboard alive across the legacy-support handoff.
            uint tick = 0;
            byte* fb = stackalloc byte[48];
            while (true)
            {
                XhciController.ProcessEvents();
                UsbHidKeyboard.HandleTransferEvents();

                if (initialized && tick == 0)
                {
                    if (!UsbHidKeyboard.Enumerated)
                    {
                        uint stage;
                        if (FrontierTests.XhciTest.TryEnumerate(out stage))
                        {
                            FrontierTests.XhciTest.EmitPass();
                        }
                        else if (stage == 0)
                        {
                            // Skippable port disconnect during reset; retry immediately.
                            tick = RetryCadence - 1;
                        }
                        else
                        {
                            byte* fp = fb;
                            fp = AppendStr(fp, "[XHCI] ENUM FAILED stage=");
                            fp = AppendDec(fp, stage);
                            *fp++ = (byte)'\n';
                            *fp = 0;
                            SyscallWrappers.Log(fb);
                        }
                    }
                    else if (!XhciController.PortStillConnected())
                    {
                        // Hot-unplug: drop back to the retry state so a later
                        // replug re-enumerates without a reboot.
                        UsbHidKeyboard.Enumerated = false;
                        SyscallWrappers.Log("[XHCI] keyboard disconnected; awaiting reconnect.\n");
                    }
                }

                tick++;
                if (tick >= RetryCadence) tick = 0;

                SyscallWrappers.Yield();
            }
        }


        private static byte* AppendStr(byte* p, string s)
        {
            for (int i = 0; i < s.Length; i++) p[i] = (byte)s[i];
            return p + s.Length;
        }


        private static byte* AppendDec(byte* p, uint value)
        {
            if (value == 0)
            {
                *p++ = (byte)'0';
                return p;
            }
            byte* tmp = stackalloc byte[12];
            int n = 0;
            while (value > 0)
            {
                tmp[n++] = (byte)('0' + (value % 10));
                value /= 10;
            }
            while (n > 0)
            {
                *p++ = tmp[--n];
            }
            return p;
        }
    }
}
