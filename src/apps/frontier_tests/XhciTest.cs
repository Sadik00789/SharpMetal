using System;
using Userland.Runtime.ZeroAlloc.Interop;
using Xhci;

namespace FrontierTests
{
    /// <summary>
    /// Frontier 4 self-test. Enumeration is exposed as a single non-blocking
    /// attempt so the driver can retry on a schedule and re-enumerate after a
    /// hot-plug; the milestone token is emitted exactly once on first success.
    /// </summary>
    public static unsafe class XhciTest
    {
        public static bool PassEmitted;

        /// <summary>
        /// One enumeration attempt. On failure <paramref name="stage"/> reports
        /// where it stopped so real-hardware bisect is possible from serial:
        /// 1 = no connected port, 2 = port reset, 3 = port enable, 4 = enumerate.
        /// </summary>
        public static bool TryEnumerate(out uint stage)
        {
            stage = 0;

            uint portId = XhciController.FindConnectedPort();
            if (portId == 0 && XhciController.HasPendingPortChange)
            {
                uint psc = XhciController.PendingPortId;
                XhciController.HasPendingPortChange = false;
                if (psc >= 1 && psc <= XhciController.MaxPorts) portId = psc;
            }

            if (portId == 0)
            {
                stage = 1;
                SyscallWrappers.Log("[XHCI] FAIL CONNECT\n");
                return false;
            }

            XhciController.RootPortId = portId;

            if (!XhciController.WaitPortConnected(portId, 2000000))
            {
                stage = 2;
                return false;
            }
            if (!XhciController.ResetPort(portId))
            {
                stage = 2;
                return false;
            }
            if (!XhciController.WaitPortEnabled(portId, 500000))
            {
                stage = 3;
                return false;
            }
            if (!UsbHidKeyboard.Enumerate(portId))
            {
                stage = 4;
                return false;
            }

            return true;
        }

        /// <summary>Emits the milestone token exactly once.</summary>
        public static void EmitPass()
        {
            if (PassEmitted) return;
            PassEmitted = true;
            SyscallWrappers.Log("[PASS] XHCI: Controller initialized and USB keyboard addressed\n");
        }
    }
}
