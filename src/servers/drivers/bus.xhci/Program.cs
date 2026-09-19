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

            bool initialized = XhciController.Initialize();

            // MSI-X vector 0x32 stays registered in the kernel InterruptDispatcher
            // (Notification Slot 15) so events are routed and EOI'd on the kernel
            // path. The driver nevertheless drains the Event Ring unconditionally
            // rather than blocking in RecvAny: input must never depend on
            // interrupt delivery succeeding, which is the guarantee that keeps
            // the keyboard alive across the legacy-support handoff.
            uint tick = 0;
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
                        else
                        {
                            SyscallWrappers.Log("[XHCI] ENUM FAILED stage=");
                            LogDec(stage);
                            SyscallWrappers.Log("\n");
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

        private static void LogDec(uint value)
        {
            byte* buf = stackalloc byte[12];
            int idx = 0;
            if (value == 0)
            {
                buf[idx++] = (byte)'0';
            }
            else
            {
                byte* tmp = stackalloc byte[12];
                int n = 0;
                while (value > 0 && n < 10)
                {
                    tmp[n++] = (byte)('0' + (value % 10));
                    value /= 10;
                }
                while (n > 0) buf[idx++] = tmp[--n];
            }
            buf[idx] = 0;
            SyscallWrappers.Log(buf);
        }
    }
}
