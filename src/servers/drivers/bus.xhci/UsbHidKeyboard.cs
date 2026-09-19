using System;
using Microkernel.Abstractions.Input;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Xhci
{
    /// <summary>
    /// USB HID boot-protocol keyboard driver.
    ///
    /// Boot reports carry HID Usage IDs (Keyboard/Keypad page 0x07), not PS/2
    /// Set-1 scancodes, so translation uses a dedicated usage table and never
    /// the PS/2 table. Translated characters are forwarded to input.hid through
    /// the IInputService.InjectKey RPC.
    /// </summary>
    public static unsafe class UsbHidKeyboard
    {
        public static bool Enumerated;
        public static uint SlotId;
        public static byte Ep1EndpointAddress;
        public static ushort Ep1MaxPacketSize = 8;
        public static byte Ep1Interval = 10;
        public static byte InterfaceNumber;
        public static uint Ep0MaxPacket = 8;

        private const int CtrlTransferMax = 256;

        // Modifier bits in report byte 0
        private const byte ModLeftCtrl = 0x01;
        private const byte ModLeftShift = 0x02;
        private const byte ModRightShift = 0x20;

        // ---- Descriptor layouts (byte offsets) ----
        // Device descriptor
        private const int DevMaxPacketSize0 = 7;
        // Configuration descriptor
        private const int CfgTotalLength = 2;
        private const int CfgValue = 5;
        // Interface descriptor
        private const int IfNumber = 2;
        private const int IfClass = 5;
        private const int IfSubClass = 6;
        private const int IfProtocol = 7;
        // Endpoint descriptor
        private const int EpAddress = 2;
        private const int EpAttributes = 3;
        private const int EpMaxPacket = 4;
        private const int EpInterval = 6;


        public static bool Enumerate(uint portId)
        {
            uint portSpeed = XhciRegisters.PortSpeed(XhciController.ReadPortSc(portId));
            Ep0MaxPacket = XhciRegisters.MaxPacketBytesForSpeed(portSpeed);
            if (Ep0MaxPacket == 255) Ep0MaxPacket = 64; // SuperSpeed default for BSR

            SyscallWrappers.Log("[XHCI] port speed=");
            LogHex(portSpeed);
            SyscallWrappers.Log("\n");
            // Low-speed devices need a longer control-transfer window.
            XhciController.ControlTimeoutIterations = (portSpeed == 2) ? 1000000 : 500000;

            // 1. Enable Slot
            Trb enableSlot;
            enableSlot.Parameter = 0;
            enableSlot.Status = 0;
            enableSlot.Control = XhciRegisters.TrbEnableSlot << (int)XhciRegisters.TrbTypeShift;
            uint slotCode = XhciController.IssueCommand(&enableSlot);
            if (slotCode != XhciRegisters.CcSuccess)
            {
                LogCode("[XHCI] Enable Slot failed, code ", slotCode);
                return false;
            }
            SlotId = XhciController.LastCompletionSlot;
            if (SlotId == 0 || SlotId > XhciController.MaxSlots)
            {
                SyscallWrappers.Log("[XHCI] Enable Slot returned an invalid slot id.\n");
                return false;
            }
            XhciController.SlotId = SlotId;
            XhciController.RootPortId = portId;

            // 2. Address Device. The root hub port number belongs in Slot
            //    Context DW1 bits 23:16 with Number of Ports (31:24) left zero:
            //    a consumer that reads (dw1 >> 16) unmasked would otherwise
            //    fold the Number of Ports byte into the port and reject the TRB
            //    with a TRB Error and no Host System Error. The EP0 Endpoint
            //    Type encoding is probed because implementations disagree; the
            //    completion code is authoritative.
            // EP0 Endpoint Type is the Control encoding, per the specification.
            bool addressed = false;
            {
                uint ep0Type = XhciRegisters.EpTypeControl;
                uint code = AddressDevice(portId, ep0Type, useBsr: false);
                if (code == XhciRegisters.CcSuccess)
                {
                    addressed = true;
                    XhciController.Ep0ControlType = ep0Type;
                    SyscallWrappers.Log("[XHCI] Address Device succeeded.\n");
                }
                else
                {
                    LogCode("[XHCI] FAIL ADDRDEV code=", code);
                    XhciController.DumpFailure(code);
                }
            }
            if (!addressed)
            {
                SyscallWrappers.Log("[XHCI] Address Device failed for all EP0 type encodings.\n");
                return false;
            }

            // 3. Read and validate the device descriptor (18 bytes).
            if (!GetDescriptor(0x01, 0, 18)) return false;
            byte* desc = (byte*)XhciController.ControlBufferVirt;
            byte bMaxPacket0 = desc[DevMaxPacketSize0];
            uint speed = XhciRegisters.PortSpeed(XhciController.ReadPortSc(portId));
            byte newMaxPacket = speed >= 4 ? (byte)(1 << (bMaxPacket0 & 0x0F)) : bMaxPacket0;
            if (newMaxPacket != 0 && newMaxPacket != Ep0MaxPacket)
            {
                Ep0MaxPacket = newMaxPacket;
                // Re-address with the corrected packet size (BSR = 0).
                uint code = AddressDevice(portId, XhciController.Ep0ControlType, false);
                if (code != XhciRegisters.CcSuccess)
                {
                    LogCode("[XHCI] FAIL ADDRDEV code=", code);
                    XhciController.DumpFailure(code);
                    return false;
                }
            }

            // 4. Read the configuration descriptor header, then the full body.
            if (!GetDescriptor(0x02, 0, 9)) return false;
            ushort totalLength = *(ushort*)((byte*)XhciController.ControlBufferVirt + CfgTotalLength);
            if (totalLength == 0 || totalLength > CtrlTransferMax) totalLength = 34;
            if (!GetDescriptor(0x02, 0, totalLength)) return false;

            byte cfgValue = 0;
            bool foundBootKeyboard = WalkConfiguration(totalLength, out cfgValue);
            if (!foundBootKeyboard)
            {
                SyscallWrappers.Log("[XHCI] No HID boot keyboard interface found on the device.\n");
                return false;
            }

            // 5. SET_CONFIGURATION
            if (!SetConfiguration(cfgValue)) return false;

            // 6. Switch the HID interface to Boot Protocol, idle rate 0.
            if (!SetProtocolBoot()) return false;
            SetIdle();

            // 7. Add the interrupt IN endpoint via Configure Endpoint.
            if (ConfigureEndpoint(portId) != XhciRegisters.CcSuccess) return false;

            // 8. Arm the first Interrupt IN transfer.
            XhciController.SubmitInterruptIn(XhciController.ReportBufferPhys, Ep1MaxPacketSize);

            XhciController.ResetFailureDump();
            Enumerated = true;
            SyscallWrappers.Log("[XHCI] USB keyboard addressed and switched to boot protocol.\n");
            return true;
        }

        private static uint AddressDevice(uint portId, uint ep0Type, bool useBsr)
        {
            XhciController.ClearInputContextAddFlags();
            XhciController.SetInputAddFlags(
                XhciRegisters.AddSlotContext | XhciRegisters.AddEndpoint0);

            uint portSc = XhciController.ReadPortSc(portId);
            uint speed = XhciRegisters.SlotSpeedForPortSpeed(XhciRegisters.PortSpeed(portSc));
            ushort mps = (ushort)Ep0MaxPacket;

            XhciController.SetSlotContext(XhciController.InputSlotContext(), speed, portId, 1);
            XhciController.SetEndpointContext(
                XhciController.InputEndpoint0(), ep0Type, mps, 0, XhciController.Ep0RingPhys);

            XhciController.Dcbaa[XhciController.SlotId] = XhciController.DeviceContextPhys;

            Trb cmd;
            cmd.Parameter = XhciController.InputContextPhys;
            cmd.Status = 0;
            cmd.Control = (XhciRegisters.TrbAddressDevice << (int)XhciRegisters.TrbTypeShift)
                        | (useBsr ? XhciRegisters.TrbBsr : 0u)
                        | (XhciController.SlotId << 24);
            return XhciController.IssueCommand(&cmd);
        }

        private static byte* AppStr(byte* p, string s)
        {
            for (int i = 0; i < s.Length; i++) p[i] = (byte)s[i];
            return p + s.Length;
        }

        private static byte* AppHex(byte* p, ulong v)
        {
            *p++ = (byte)'0';
            *p++ = (byte)'x';
            bool started = false;
            for (int shift = 60; shift >= 0; shift -= 4)
            {
                int n = (int)((v >> shift) & 0xF);
                if (n != 0 || started || shift == 0)
                {
                    started = true;
                    *p++ = (byte)(n < 10 ? ('0' + n) : ('A' + n - 10));
                }
            }
            return p;
        }

        /// <summary>Single-syscall diagnostic line so serial output cannot interleave.</summary>
        private static void LogAddrDev(uint slot, uint type, uint mps, uint speed, uint port,
            ulong icp, uint* ic)
        {
            byte* b = stackalloc byte[200];
            byte* p = b;
            p = AppStr(p, "[XHCI] AD s="); p = AppHex(p, slot);
            p = AppStr(p, " t="); p = AppHex(p, type);
            p = AppStr(p, " m="); p = AppHex(p, mps);
            p = AppStr(p, " sp="); p = AppHex(p, speed);
            p = AppStr(p, " pt="); p = AppHex(p, port);
            p = AppStr(p, " ic="); p = AppHex(p, ic[1]);
            p = AppStr(p, " s0="); p = AppHex(p, ic[8]);
            p = AppStr(p, " s2="); p = AppHex(p, ic[10]);
            p = AppStr(p, " e0="); p = AppHex(p, ic[16]);
            p = AppStr(p, " e1="); p = AppHex(p, ic[17]);
            p = AppStr(p, " e2="); p = AppHex(p, ic[18]);
            p = AppStr(p, " icp="); p = AppHex(p, icp);
            p = AppStr(p, " gia="); p = AppHex(p, SyscallWrappers.GetPhysicalAddress(XhciController.InputContextVirt));
            p = AppStr(p, " gdev="); p = AppHex(p, SyscallWrappers.GetPhysicalAddress(XhciController.DeviceContextVirt));
            p = AppStr(p, " gdba="); p = AppHex(p, SyscallWrappers.GetPhysicalAddress(XhciController.DcbaaVirt));
            *p++ = (byte)'\n';
            *p = 0;
            SyscallWrappers.Log(b);
        }

        private static uint ConfigureEndpoint(uint portId)
        {
            XhciController.ClearInputContextAddFlags();
            XhciController.SetInputAddFlags(
                XhciRegisters.AddSlotContext | XhciRegisters.AddEndpoint1In);

            uint speed = XhciRegisters.SlotSpeedForPortSpeed(
                XhciRegisters.PortSpeed(XhciController.ReadPortSc(portId)));
            // Context Entries = 3: Slot, EP0 (DCI 1), EP1 IN (DCI 3)
            XhciController.SetSlotContext(XhciController.InputSlotContext(), speed, portId, 3);

            // Probe the interrupt IN endpoint type encoding as well.
            uint acceptedType = XhciRegisters.EpTypeInterruptIn;
            for (int attempt = 0; attempt < XhciRegisters.EndpointTypeAttempts; attempt++)
            {
                uint epType = XhciRegisters.InterruptInTypeForAttempt(attempt);
                XhciController.SetEndpointContext(
                    XhciController.InputEndpoint(3), epType, Ep1MaxPacketSize,
                    EncodeInterval(speed, Ep1Interval),
                    XhciController.Ep1RingPhys);

                Trb cmd;
                cmd.Parameter = XhciController.InputContextPhys;
                cmd.Status = 0;
                cmd.Control = (XhciRegisters.TrbConfigureEndpoint << (int)XhciRegisters.TrbTypeShift)
                            | (XhciController.SlotId << 24);
                uint code = XhciController.IssueCommand(&cmd);
                if (code == XhciRegisters.CcSuccess)
                {
                    acceptedType = epType;
                    return XhciRegisters.CcSuccess;
                }
                LogCode("[XHCI] Configure Endpoint attempt failed, code ", code);
            }

            SyscallWrappers.Log("[XHCI] Configure Endpoint failed for all interrupt IN encodings.\n");
            return 0;
        }

        /// <summary>
        /// Encodes the endpoint descriptor bInterval into the Endpoint Context
        /// interval field, which is speed dependent:
        ///  - FS/LS (speed 1,2): floor(log2(bInterval)) + 3
        ///  - HS    (speed 3):   bInterval - 1
        ///  - SS+   (speed >= 4): bInterval (already an exponent)
        /// </summary>
        public static byte EncodeInterval(uint speed, byte bInterval)
        {
            if (bInterval < 1) bInterval = 1;

            if (speed == 1 || speed == 2)
            {
                uint v = bInterval;
                uint log = 0;
                while (v > 1) { v >>= 1; log++; }
                return (byte)(log + 3);
            }
            if (speed == 3)
            {
                return (byte)(bInterval - 1);
            }
            return bInterval;
        }

        private static bool GetDescriptor(byte type, byte index, ushort length)
        {
            uint granted;
            return XhciController.ControlTransfer(
                XhciRegisters.BmRequestIn | XhciRegisters.BmRequestTypeStandard | XhciRegisters.BmRecipientDevice,
                XhciRegisters.ReqGetDescriptor,
                (ushort)((type << 8) | index),
                0,
                length,
                XhciController.ControlBufferVirt,
                XhciController.ControlBufferPhys,
                out granted);
        }

        private static bool SetConfiguration(byte configurationValue)
        {
            uint granted;
            return XhciController.ControlTransfer(
                XhciRegisters.BmRequestTypeStandard | XhciRegisters.BmRecipientDevice,
                XhciRegisters.ReqSetConfiguration,
                configurationValue,
                0,
                0,
                XhciController.ControlBufferVirt,
                XhciController.ControlBufferPhys,
                out granted);
        }

        private static bool SetProtocolBoot()
        {
            uint granted;
            return XhciController.ControlTransfer(
                XhciRegisters.BmRequestTypeClass | XhciRegisters.BmRecipientInterface,
                XhciRegisters.ReqSetProtocol,
                0, // 0 = boot protocol
                InterfaceNumber,
                0,
                XhciController.ControlBufferVirt,
                XhciController.ControlBufferPhys,
                out granted);
        }

        private static void SetIdle()
        {
            uint granted;
            XhciController.ControlTransfer(
                XhciRegisters.BmRequestTypeClass | XhciRegisters.BmRecipientInterface,
                XhciRegisters.ReqSetIdle,
                0,
                InterfaceNumber,
                0,
                XhciController.ControlBufferVirt,
                XhciController.ControlBufferPhys,
                out granted);
        }

        /// <summary>
        /// Walks the configuration descriptor looking for a HID boot keyboard
        /// interface and its interrupt IN endpoint.
        /// </summary>
        private static bool WalkConfiguration(ushort totalLength, out byte configurationValue)
        {
            configurationValue = 0;
            byte* cfg = (byte*)XhciController.ControlBufferVirt;
            configurationValue = cfg[CfgValue];

            bool sawHidKeyboardInterface = false;
            ushort offset = 0;
            int guard = 64;

            while (offset + 2 <= totalLength && guard-- > 0)
            {
                byte length = cfg[offset];
                byte type = cfg[offset + 1];
                if (length < 2) break;

                if (type == XhciRegisters.DescriptorTypeInterface && offset + 9 <= totalLength)
                {
                    byte ifaceClass = cfg[offset + IfClass];
                    byte ifaceSub = cfg[offset + IfSubClass];
                    byte ifaceProto = cfg[offset + IfProtocol];
                    if (ifaceClass == 0x09)
                    {
                        SyscallWrappers.Log("[XHCI] hub detected; unsupported\n");
                        return false;
                    }
                    if (ifaceClass == XhciRegisters.HidClass &&
                        ifaceSub == XhciRegisters.HidSubclassBoot &&
                        ifaceProto == XhciRegisters.HidProtocolKeyboard)
                    {
                        sawHidKeyboardInterface = true;
                        InterfaceNumber = cfg[offset + IfNumber];
                    }
                }
                else if (type == XhciRegisters.DescriptorTypeEndpoint && offset + 7 <= totalLength)
                {
                    byte address = cfg[offset + EpAddress];
                    byte attributes = cfg[offset + EpAttributes];
                    if (sawHidKeyboardInterface &&
                        (attributes & 0x03) == XhciRegisters.EpAttrInterrupt &&
                        (address & 0x80) != 0)
                    {
                        Ep1EndpointAddress = address;
                        Ep1MaxPacketSize = *(ushort*)(cfg + offset + EpMaxPacket);
                        Ep1Interval = cfg[offset + EpInterval];
                        if (Ep1MaxPacketSize == 0) Ep1MaxPacketSize = 8;
                    }
                }

                offset += length;
            }

            return sawHidKeyboardInterface && Ep1EndpointAddress != 0;
        }

        // =====================================================================
        // Report handling
        // =====================================================================
        private struct ReportState
        {
            public fixed byte Previous[8];
        }

        private static ReportState s_state;
        private static byte s_lastModifiers;
        private static bool s_loggedFirstTransfer;
        private static bool s_loggedFirstKey;

        public static void HandleTransferEvents()
        {
            if (!Enumerated) return;
            if (!XhciController.TransferEventValid) return;

            uint ep = XhciController.TransferEventEpId;
            uint code = XhciController.TransferEventCode;
            XhciController.TransferEventValid = false;

            if (!s_loggedFirstTransfer)
            {
                s_loggedFirstTransfer = true;
                SyscallWrappers.Log("[XHCI] transfer event ep=");
                LogHex(ep);
                SyscallWrappers.Log(" code=");
                LogHex(code);
                SyscallWrappers.Log("\n");
            }

            if (ep != 3) return;

            if (code == XhciRegisters.CcSuccess || code == XhciRegisters.CcShortPacket)
            {
                byte* report = (byte*)XhciController.ReportBufferVirt;
                ProcessReport(report);

                // Re-arm immediately so keystrokes are never dropped.
                XhciController.SubmitInterruptIn(XhciController.ReportBufferPhys, Ep1MaxPacketSize);
            }
            else
            {
                // Stall or error: re-arm anyway and keep going.
                XhciController.SubmitInterruptIn(XhciController.ReportBufferPhys, Ep1MaxPacketSize);
            }
        }

        private static void ProcessReport(byte* report)
        {
            byte modifiers = report[0];
            bool shift = (modifiers & (ModLeftShift | ModRightShift)) != 0;
            s_lastModifiers = modifiers;

            fixed (byte* prev = s_state.Previous)
            {
                for (int i = 2; i < 8; i++)
                {
                    byte usage = report[i];
                    if (usage == 0) continue;

                    // Skip usages already held in the previous report.
                    bool alreadyHeld = false;
                    for (int j = 2; j < 8; j++)
                    {
                        if (prev[j] == usage) { alreadyHeld = true; break; }
                    }
                    if (alreadyHeld) continue;

                    if (!s_loggedFirstKey)
                    {
                        s_loggedFirstKey = true;
                        SyscallWrappers.Log("[XHCI] HID key usage received.\n");
                    }
                    EmitUsage(usage, shift);
                }

                for (int i = 0; i < 8; i++) prev[i] = report[i];
            }
        }

        private static void EmitUsage(byte usage, bool shift)
        {
            RawKeyEvent evt;
            evt.Modifiers = s_lastModifiers;
            evt.UsageId = usage;
            evt.Pressed = 1;
            evt.Ascii = HidUsageToAscii(usage, shift);

            // Navigation keys expand to the shell's existing escape sequences.
            switch (usage)
            {
                case 0x52: // Up
                    Inject(0x1B); Inject((byte)'['); Inject((byte)'A');
                    return;
                case 0x51: // Down
                    Inject(0x1B); Inject((byte)'['); Inject((byte)'B');
                    return;
                case 0x50: // Left
                    Inject(0x1B); Inject((byte)'['); Inject((byte)'D');
                    return;
                case 0x4F: // Right
                    Inject(0x1B); Inject((byte)'['); Inject((byte)'C');
                    return;
            }

            if (evt.Ascii != 0)
            {
                Inject(evt.Ascii);
            }
        }

        private static void Inject(uint key)
        {
            if (key == 0) return;
            var inputClient = new InputServiceClient(endpointCptr: 10);
            inputClient.InjectKey(key);
        }

        /// <summary>
        /// HID Usage ID (Keyboard/Keypad page) to ASCII. Shift and caps lock
        /// affect letters; shift affects the number row and punctuation.
        /// </summary>
        public static uint HidUsageToAscii(byte usage, bool shift)
        {
            bool caps = false; // Caps lock tracking is left to the host layout.
            bool upper = shift ^ caps;

            // Letters a-z
            if (usage >= 0x04 && usage <= 0x1D)
            {
                byte lower = (byte)('a' + (usage - 0x04));
                return upper ? (uint)(lower - 32) : lower;
            }

            // Digits 1-9 and 0
            if (usage >= 0x1E && usage <= 0x26)
            {
                byte digit = (byte)('1' + (usage - 0x1E));
                if (shift) return ShiftedDigit(usage);
                return digit;
            }
            if (usage == 0x27)
            {
                return shift ? (uint)')' : (uint)'0';
            }

            switch (usage)
            {
                case 0x28: return '\n';       // Enter
                case 0x29: return 0x1B;       // Escape
                case 0x2A: return '\b';       // Backspace
                case 0x2B: return '\t';       // Tab
                case 0x2C: return ' ';        // Space
                case 0x2D: return shift ? (uint)'_' : (uint)'-';
                case 0x2E: return shift ? (uint)'+' : (uint)'=';
                case 0x2F: return shift ? (uint)'{' : (uint)'[';
                case 0x30: return shift ? (uint)'}' : (uint)']';
                case 0x31: return shift ? (uint)'|' : (uint)'\\';
                case 0x33: return shift ? (uint)':' : (uint)';';
                case 0x34: return shift ? (uint)'"' : (uint)'\'';
                case 0x35: return shift ? (uint)'~' : (uint)'`';
                case 0x36: return shift ? (uint)'<' : (uint)',';
                case 0x37: return shift ? (uint)'>' : (uint)'.';
                case 0x38: return shift ? (uint)'?' : (uint)'/';
            }

            return 0;
        }

        private static uint ShiftedDigit(byte usage)
        {
            switch (usage)
            {
                case 0x1E: return '!';
                case 0x1F: return '@';
                case 0x20: return '#';
                case 0x21: return '$';
                case 0x22: return '%';
                case 0x23: return '^';
                case 0x24: return '&';
                case 0x25: return '*';
                case 0x26: return '(';
            }
            return 0;
        }

        private static void LogHex(ulong value)
        {
            byte* buf = stackalloc byte[20];
            buf[0] = (byte)'0';
            buf[1] = (byte)'x';
            int idx = 2;
            bool started = false;
            for (int shift = 60; shift >= 0; shift -= 4)
            {
                int nibble = (int)((value >> shift) & 0xF);
                if (nibble != 0 || started || shift == 0)
                {
                    started = true;
                    buf[idx++] = (byte)(nibble < 10 ? ('0' + nibble) : ('A' + nibble - 10));
                }
            }
            buf[idx] = 0;
            SyscallWrappers.Log(buf);
        }

        private static void LogCode(string prefix, uint code)
        {
            SyscallWrappers.Log(prefix);
            byte* buf = stackalloc byte[4];
            buf[0] = (byte)('0' + (code / 10));
            buf[1] = (byte)('0' + (code % 10));
            buf[2] = (byte)'\n';
            buf[3] = 0;
            // Keep it simple: only single/double digit codes are formatted.
            if (code >= 100)
            {
                SyscallWrappers.Log("?\n");
                return;
            }
            SyscallWrappers.Log(buf);
        }
    }
}
