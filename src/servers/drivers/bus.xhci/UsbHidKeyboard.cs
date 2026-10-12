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
        public static uint SlotId => XhciController.ActiveSlotId;
        public static byte Ep1EndpointAddress;
        public static ushort Ep1MaxPacketSize = 8;
        public static byte Ep1Interval = 10;
        public static byte InterfaceNumber;
        public static uint Ep0MaxPacket = 8;

        public static bool BootProtocolActive;
        public static int ModifierByteOffset = 0;
        public static int KeyArrayStart = 2;
        public static int KeyArrayEnd = 7;
        public static bool HasReportId = false;
        public static byte ExpectedReportId = 0;

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
            uint stage;
            return Enumerate(portId, out stage);
        }

        public static bool Enumerate(uint portId, out uint stage)
        {
            stage = 4;
            uint portSc = XhciController.ReadPortSc(portId);
            uint portSpeed = (portSc >> 10) & 0x0F;
            if (portSpeed == 0 || portSpeed > 5)
            {
                byte* ib = stackalloc byte[64];
                byte* ip = ib;
                ip = AppStr(ip, "[XHCI] invalid port speed ");
                ip = AppHex(ip, portSpeed);
                ip = AppStr(ip, "; aborting enum\n");
                *ip = 0;
                SyscallWrappers.Log(ib);
                stage = 2;
                return false;
            }

            Ep0MaxPacket = XhciRegisters.Ep0MaxPacketForSpeed(portSpeed);



            // Low-speed devices need a longer control-transfer window.
            XhciController.ControlTimeoutIterations = (portSpeed == 2) ? 1000000 : 500000;

            // Settle delay after initial port reset (TRSTRCY reset recovery time).
            for (int y = 0; y < XhciController.SettleTimeoutIterations; y++) SyscallWrappers.Yield();

            bool validated = false;
            uint* candTypes = stackalloc uint[XhciRegisters.EndpointTypeAttempts];
            uint* candAddrCc = stackalloc uint[XhciRegisters.EndpointTypeAttempts];
            uint* candLiveCc = stackalloc uint[XhciRegisters.EndpointTypeAttempts];

            byte* bLoop = stackalloc byte[128];
            byte* bEvt = stackalloc byte[160];
            byte* bEp0 = stackalloc byte[64];

            for (int attempt = 0; attempt < XhciRegisters.EndpointTypeAttempts; attempt++)
            {
                uint ep0Type = XhciRegisters.Ep0TypeForAttempt(attempt);
                candTypes[attempt] = ep0Type;
                candAddrCc[attempt] = 0;
                candLiveCc[attempt] = 0;

                // a) If this is not the first attempt: issue Disable Slot for the active slot,
                //    re-reset the port (ResetPort + WaitPortEnabled), then settle: bounded yield loop ~50 ms.
                if (attempt > 0)
                {
                    if (XhciController.ActiveSlotId != 0)
                    {
                        XhciController.DisableSlot(XhciController.ActiveSlotId);
                    }

                    if (!XhciController.ResetPort(portId) || !XhciController.WaitPortEnabled(portId, 500000))
                    {
                        candAddrCc[attempt] = 0xFF;
                        continue;
                    }

                    for (int y = 0; y < XhciController.SettleTimeoutIterations; y++) SyscallWrappers.Yield();
                }

                // b) Enable Slot fresh (new SlotId), rebuild DCBAA entry and EP0 ring.
                uint slotCode = XhciController.EnableSlot(out uint newSlotId);
                if (slotCode != XhciRegisters.CcSuccess || newSlotId == 0 || newSlotId > XhciController.MaxSlots)
                {
                    byte* pSlot = bLoop;
                    pSlot = AppStr(pSlot, "[XHCI] Enable Slot failed, code ");
                    pSlot = AppHex(pSlot, slotCode);
                    *pSlot++ = (byte)'\n';
                    *pSlot = 0;
                    SyscallWrappers.Log(bLoop);
                    candAddrCc[attempt] = slotCode;
                    continue;
                }

                XhciController.ActiveSlotId = newSlotId;
                XhciController.RootPortId = portId;
                XhciController.RebuildSlotState(newSlotId);

                // c) Address Device with candidate type (BSR=0), log attempt+code as today.
                uint code = AddressDevice(portId, ep0Type, useBsr: false, newSlotId);
                candAddrCc[attempt] = code;


                if (code == XhciRegisters.CcSuccess)
                {
                    XhciController.DumpOutputContext(XhciController.ActiveSlotId);

                    XhciController.Mfence();
                    uint* devCtx = XhciController.DeviceContext;
                    uint slotDw3 = (devCtx != null) ? devCtx[3] : 0;
                    uint stateSpec = (slotDw3 >> 27) & 0x1Fu;
                    uint state19 = (slotDw3 >> 19) & 0x1Fu;
                    uint stateAlt = (slotDw3 >> 24) & 0xFu;

                    uint state = stateSpec;
                    if (state < 2 && state19 >= 2) state = state19;
                    if (state < 2 && stateAlt >= 2) state = stateAlt;

                    if (state < 2)
                    {
                        byte* rp = bLoop;
                        rp = AppStr(rp, "[XHCI] slot state < 2; rejecting candidate\n");
                        *rp = 0;
                        SyscallWrappers.Log(bLoop);
                        continue;
                    }
                }

                if (code != XhciRegisters.CcSuccess)
                {
                    XhciController.DumpFailure(code);
                    continue;
                }


                // d) LIVENESS CHECK: issue GET_DESCRIPTOR(Device, index 0, length 8) with
                //    short timeout (1/4 of ControlTimeoutIterations, min 10000), silent=true.
                uint granted = 0;
                uint liveCc = 0;
                int shortTimeout = XhciController.ControlTimeoutIterations / 4;
                if (shortTimeout < 10000) shortTimeout = 10000;

                bool liveOk = XhciController.ControlTransfer(
                    XhciRegisters.BmRequestIn | XhciRegisters.BmRequestTypeStandard | XhciRegisters.BmRecipientDevice,
                    XhciRegisters.ReqGetDescriptor,
                    (ushort)((0x01 << 8) | 0), // Device descriptor, index 0
                    0,
                    8,
                    XhciController.ControlBufferVirt,
                    XhciController.ControlBufferPhys,
                    out granted,
                    timeoutIterations: shortTimeout,
                    silent: true,
                    outCompletionCode: &liveCc);

                candLiveCc[attempt] = liveCc;

                if (liveOk)
                {
                    validated = true;
                    XhciController.Ep0ControlType = ep0Type;


                    break;
                }
                else
                {
                    // TASK 2: Raw transfer event dump on liveness failure
                    Trb evtToDump = XhciController.LastTransferEvent;
                    if (evtToDump.Control == 0 && XhciController.LastRawEvent.Control != 0)
                    {
                        evtToDump = XhciController.LastRawEvent;
                    }
                    uint evtDw0 = (uint)(evtToDump.Parameter & 0xFFFFFFFFUL);
                    uint evtDw1 = (uint)(evtToDump.Parameter >> 32);
                    uint evtDw2 = evtToDump.Status;
                    uint evtDw3 = evtToDump.Control;

                    byte* pEvt = bEvt;
                    pEvt = AppStr(pEvt, "[XHCI] evt dw0=");
                    pEvt = AppHex(pEvt, evtDw0);
                    pEvt = AppStr(pEvt, " dw1=");
                    pEvt = AppHex(pEvt, evtDw1);
                    pEvt = AppStr(pEvt, " dw2=");
                    pEvt = AppHex(pEvt, evtDw2);
                    pEvt = AppStr(pEvt, " dw3=");
                    pEvt = AppHex(pEvt, evtDw3);
                    *pEvt++ = (byte)'\n';
                    *pEvt = 0;
                    SyscallWrappers.Log(bEvt);

                    // TASK 3: EP0 state re-read after liveness failure
                    XhciController.Mfence();
                    uint* ep0Ctx = (uint*)((byte*)XhciController.DeviceContext + XhciController.CtxSize);
                    uint ep0State = (ep0Ctx != null) ? (ep0Ctx[0] & 0x7u) : 0u;

                    byte* sp = bEp0;
                    sp = AppStr(sp, "[XHCI] ep0 state after live-fail=");
                    sp = AppHex(sp, ep0State);
                    *sp++ = (byte)'\n';
                    *sp = 0;
                    SyscallWrappers.Log(bEp0);

                    byte* pRej = bLoop;
                    pRej = AppStr(pRej, "[XHCI] EP0 type ");
                    pRej = AppHex(pRej, ep0Type);
                    pRej = AppStr(pRej, " addressed but control dead (cc=");
                    pRej = AppHex(pRej, liveCc);
                    pRej = AppStr(pRej, "); rejecting\n");
                    *pRej = 0;
                    SyscallWrappers.Log(bLoop);

                    XhciController.DumpFailure(liveCc);
                    continue;
                }
            }

            if (!validated)
            {
                if (XhciController.ActiveSlotId != 0)
                {
                    XhciController.DisableSlot(XhciController.ActiveSlotId);
                }


                stage = 4;
                return false;
            }

            // 3. EP0 Max Packet Size Discovery
            if (!GetDescriptor(0x01, 0, 8)) return false;
            byte* desc = (byte*)XhciController.ControlBufferVirt;
            byte bMaxPacket0 = desc[DevMaxPacketSize0];
            uint speed = (XhciController.ReadPortSc(portId) >> 10) & 0x0F;


            if (bMaxPacket0 != 64 && bMaxPacket0 != 0)
            {
                uint newMaxPacket = speed >= 4 ? (1u << (bMaxPacket0 & 0x0F)) : (uint)bMaxPacket0;
                if (newMaxPacket == 0) newMaxPacket = 8;
                Ep0MaxPacket = newMaxPacket;
                uint evalCode = XhciController.EvaluateContextEp0((ushort)newMaxPacket);
                if (evalCode != XhciRegisters.CcSuccess)
                {
                    byte* fb = stackalloc byte[64];
                    byte* fp = fb;
                    fp = AppStr(fp, "[XHCI] FAIL EVAL CONTEXT code=");
                    fp = AppHex(fp, evalCode);
                    *fp++ = (byte)'\n';
                    *fp = 0;
                    SyscallWrappers.Log(fb);
                    XhciController.DumpFailure(evalCode);
                    return false;
                }
            }

            // 4. Read the configuration descriptor header, then the full body.
            if (!GetDescriptor(0x02, 0, 9)) return false;
            ushort totalLength = *(ushort*)((byte*)XhciController.ControlBufferVirt + CfgTotalLength);
            if (totalLength == 0 || totalLength > CtrlTransferMax) totalLength = 34;
            if (!GetDescriptor(0x02, 0, totalLength)) return false;

            byte cfgValue = 0;
            bool foundKeyboard = WalkConfiguration(totalLength, out cfgValue);
            if (!foundKeyboard)
            {
                SyscallWrappers.Log("[XHCI] No HID keyboard interface found on the device.\n");
                return false;
            }

            // 5. SET_CONFIGURATION
            if (!SetConfiguration(cfgValue)) return false;

            // 6. Switch the HID interface to Boot Protocol (best effort), idle rate 0.
            if (SetProtocolBoot())
            {
                BootProtocolActive = true;
                ModifierByteOffset = 0;
                KeyArrayStart = 2;
                KeyArrayEnd = 7;
                HasReportId = false;
                ExpectedReportId = 0;
            }
            else
            {
                BootProtocolActive = false;
                SyscallWrappers.Log("[XHCI] using Report Protocol fallback for HID device\n");
                FetchAndParseReportDescriptor();
            }
            SetIdle();

            // 7. Add the interrupt IN endpoint via Configure Endpoint.
            if (ConfigureEndpoint(portId) != XhciRegisters.CcSuccess) return false;

            // 8. Arm the first Interrupt IN transfer.
            XhciController.SubmitInterruptIn(XhciController.ReportBufferPhys, Ep1MaxPacketSize);

            XhciController.ResetFailureDump();
            Enumerated = true;
            SyscallWrappers.Log("[XHCI] USB keyboard online.\n");
            return true;
        }

        private static uint AddressDevice(uint portId, uint ep0Type, bool useBsr, uint enblSlot)
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

            XhciController.Dcbaa[XhciController.ActiveSlotId] = XhciController.DeviceContextPhys;

            Trb cmd;
            cmd.Parameter = XhciController.InputContextPhys;
            cmd.Status = 0;
            cmd.Control = (XhciRegisters.TrbAddressDevice << (int)XhciRegisters.TrbTypeShift)
                        | (useBsr ? XhciRegisters.TrbBsr : 0u)
                        | (XhciController.ActiveSlotId << 24);



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

        private static byte* AppDec(byte* p, uint value)
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

        private static byte* AppHex2(byte* p, byte val)
        {
            byte hi = (byte)((val >> 4) & 0xF);
            byte lo = (byte)(val & 0xF);
            *p++ = (byte)(hi < 10 ? ('0' + hi) : ('A' + (hi - 10)));
            *p++ = (byte)(lo < 10 ? ('0' + lo) : ('A' + (lo - 10)));
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
                            | (XhciController.ActiveSlotId << 24);
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
        /// Walks the configuration descriptor looking for any HID interface with an
        /// Interrupt IN endpoint, preferring a boot keyboard interface (subclass 1,
        /// protocol 1) if available. Hub interfaces (class 0x09) are skipped.
        /// </summary>
        private static bool WalkConfiguration(ushort totalLength, out byte configurationValue)
        {
            configurationValue = 0;
            byte* cfg = (byte*)XhciController.ControlBufferVirt;
            configurationValue = cfg[CfgValue];

            bool foundBoot = false;
            bool foundAny = false;
            byte bestIfaceNum = 0;
            byte bestEpAddr = 0;
            ushort bestEpMaxPacket = 8;
            byte bestEpInterval = 10;

            bool curIsHid = false;
            bool curIsBoot = false;
            byte curIfaceNum = 0;

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
                        // Hub interface: skip and keep scanning
                        curIsHid = false;
                        curIsBoot = false;
                    }
                    else if (ifaceClass == XhciRegisters.HidClass)
                    {
                        LogHidIface(ifaceClass, ifaceSub, ifaceProto);
                        curIsHid = true;
                        curIsBoot = (ifaceSub == XhciRegisters.HidSubclassBoot &&
                                     ifaceProto == XhciRegisters.HidProtocolKeyboard);
                        curIfaceNum = cfg[offset + IfNumber];
                    }
                    else
                    {
                        curIsHid = false;
                        curIsBoot = false;
                    }
                }
                else if (type == XhciRegisters.DescriptorTypeEndpoint && offset + 7 <= totalLength)
                {
                    byte address = cfg[offset + EpAddress];
                    byte attributes = cfg[offset + EpAttributes];
                    if (curIsHid &&
                        (attributes & 0x03) == XhciRegisters.EpAttrInterrupt &&
                        (address & 0x80) != 0)
                    {
                        ushort maxPacket = *(ushort*)(cfg + offset + EpMaxPacket);
                        if (maxPacket == 0) maxPacket = 8;
                        byte interval = cfg[offset + EpInterval];

                        if (curIsBoot)
                        {
                            foundBoot = true;
                            foundAny = true;
                            bestIfaceNum = curIfaceNum;
                            bestEpAddr = address;
                            bestEpMaxPacket = maxPacket;
                            bestEpInterval = interval;
                        }
                        else if (!foundBoot && !foundAny)
                        {
                            foundAny = true;
                            bestIfaceNum = curIfaceNum;
                            bestEpAddr = address;
                            bestEpMaxPacket = maxPacket;
                            bestEpInterval = interval;
                        }
                    }
                }

                offset += length;
            }

            if (foundAny)
            {
                InterfaceNumber = bestIfaceNum;
                Ep1EndpointAddress = bestEpAddr;
                Ep1MaxPacketSize = bestEpMaxPacket;
                Ep1Interval = bestEpInterval;
                return true;
            }

            return false;
        }

        private static void LogHidIface(byte c, byte s, byte p)
        {
            byte* b = stackalloc byte[36];
            byte* ptr = b;
            ptr = AppStr(ptr, "[XHCI] HID iface c=");
            ptr = AppHex2(ptr, c);
            ptr = AppStr(ptr, " s=");
            ptr = AppHex2(ptr, s);
            ptr = AppStr(ptr, " p=");
            ptr = AppHex2(ptr, p);
            *ptr++ = (byte)'\n';
            *ptr = 0;
            SyscallWrappers.Log(b);
        }

        private static bool GetReportDescriptor(byte ifaceNum, ushort length, out uint granted)
        {
            granted = 0;
            bool ok = XhciController.ControlTransfer(
                (byte)(XhciRegisters.BmRequestIn | XhciRegisters.BmRequestTypeStandard | XhciRegisters.BmRecipientInterface),
                XhciRegisters.ReqGetDescriptor,
                (ushort)((XhciRegisters.DescriptorTypeHidReport << 8) | 0),
                ifaceNum,
                length,
                XhciController.ControlBufferVirt,
                XhciController.ControlBufferPhys,
                out granted);
            if (!ok || granted == 0)
            {
                ok = XhciController.ControlTransfer(
                    (byte)(XhciRegisters.BmRequestIn | XhciRegisters.BmRequestTypeStandard | XhciRegisters.BmRecipientDevice),
                    XhciRegisters.ReqGetDescriptor,
                    (ushort)((XhciRegisters.DescriptorTypeHidReport << 8) | 0),
                    ifaceNum,
                    length,
                    XhciController.ControlBufferVirt,
                    XhciController.ControlBufferPhys,
                    out granted);
            }
            return ok && granted > 0;
        }

        private static void FetchAndParseReportDescriptor()
        {
            uint granted = 0;
            bool fetched = GetReportDescriptor(InterfaceNumber, 256, out granted);
            if (fetched && granted > 0)
            {
                byte* desc = (byte*)XhciController.ControlBufferVirt;
                if (ParseReportDescriptor(desc, (int)granted))
                {
                    return;
                }
            }

            SyscallWrappers.Log("[XHCI] assuming standard 8-byte HID keyboard layout\n");
            ModifierByteOffset = 0;
            KeyArrayStart = 2;
            KeyArrayEnd = 7;
            HasReportId = false;
            ExpectedReportId = 0;
        }

        private static bool ParseReportDescriptor(byte* desc, int len)
        {
            int offset = 0;
            int currentBitOffset = 0;
            uint currentUsagePage = 0;
            uint currentReportSize = 0;
            uint currentReportCount = 0;
            byte currentReportId = 0;
            bool hasReportId = false;
            uint usageMin = 0;
            uint usageMax = 0;

            bool foundMod = false;
            bool foundKeys = false;
            int modOffset = 0;
            int keyStart = 0;
            int keyEnd = 0;
            byte rpid = 0;
            bool rpidPresent = false;

            while (offset < len)
            {
                byte prefix = desc[offset];
                if (prefix == 0xFE) // Long item
                {
                    if (offset + 2 >= len) break;
                    byte longLen = desc[offset + 1];
                    offset += 3 + longLen;
                    continue;
                }

                int bSize = prefix & 0x03;
                int dataSize = bSize == 3 ? 4 : bSize;
                if (offset + 1 + dataSize > len) break;

                uint dataVal = 0;
                for (int i = 0; i < dataSize; i++)
                {
                    dataVal |= ((uint)desc[offset + 1 + i]) << (i * 8);
                }

                int bType = (prefix >> 2) & 0x03;
                int bTag = (prefix >> 4) & 0x0F;

                if (bType == 1) // Global items
                {
                    switch (bTag)
                    {
                        case 0: // Usage Page
                            currentUsagePage = dataVal;
                            break;
                        case 7: // Report Size
                            currentReportSize = dataVal;
                            break;
                        case 8: // Report ID
                            currentReportId = (byte)dataVal;
                            hasReportId = true;
                            currentBitOffset = 8;
                            if (!foundMod || !foundKeys)
                            {
                                foundMod = false;
                                foundKeys = false;
                            }
                            break;
                        case 9: // Report Count
                            currentReportCount = dataVal;
                            break;
                    }
                }
                else if (bType == 2) // Local items
                {
                    switch (bTag)
                    {
                        case 1: // Usage Minimum
                            usageMin = dataVal;
                            break;
                        case 2: // Usage Maximum
                            usageMax = dataVal;
                            break;
                    }
                }
                else if (bType == 0) // Main items
                {
                    if (bTag == 8) // Input
                    {
                        uint fieldBits = currentReportSize * currentReportCount;
                        bool isConst = (dataVal & 0x01) != 0;
                        bool isVar = (dataVal & 0x02) != 0;
                        bool isArray = !isVar;

                        if (!isConst)
                        {
                            if (!foundMod && (currentUsagePage == 0x07 || usageMin == 0xE0) && isVar && fieldBits == 8)
                            {
                                modOffset = currentBitOffset / 8;
                                foundMod = true;
                                rpid = currentReportId;
                                rpidPresent = hasReportId;
                            }
                            else if (!foundKeys && currentUsagePage == 0x07 && isArray && currentReportSize == 8 && currentReportCount >= 1)
                            {
                                keyStart = currentBitOffset / 8;
                                keyEnd = keyStart + (int)currentReportCount - 1;
                                foundKeys = true;
                                rpid = currentReportId;
                                rpidPresent = hasReportId;
                            }
                        }

                        currentBitOffset += (int)fieldBits;

                        if (foundMod && foundKeys)
                        {
                            break;
                        }
                    }

                    // Local items clear after any Main item
                    usageMin = 0;
                    usageMax = 0;
                }

                offset += 1 + dataSize;
            }

            if (foundMod && foundKeys && modOffset >= 0 && modOffset < 64 && keyStart >= 0 && keyEnd >= keyStart && keyEnd < 64)
            {
                ModifierByteOffset = modOffset;
                KeyArrayStart = keyStart;
                KeyArrayEnd = keyEnd;
                HasReportId = rpidPresent;
                ExpectedReportId = rpid;

                byte* b = stackalloc byte[80];
                byte* p = b;
                p = AppStr(p, "[XHCI] parsed HID report layout: mod=");
                p = AppHex(p, (ulong)modOffset);
                p = AppStr(p, " keys=");
                p = AppHex(p, (ulong)keyStart);
                p = AppStr(p, "..");
                p = AppHex(p, (ulong)keyEnd);
                if (rpidPresent)
                {
                    p = AppStr(p, " rpid=");
                    p = AppHex(p, rpid);
                }
                *p++ = (byte)'\n';
                *p = 0;
                SyscallWrappers.Log(b);
                return true;
            }

            return false;
        }

        // =====================================================================
        // Report handling
        // =====================================================================
        private struct ReportState
        {
            public fixed byte Previous[64];
        }

        private static ReportState s_state;
        private static byte s_lastModifiers;


        public static void HandleTransferEvents()
        {
            if (!Enumerated) return;
            if (!XhciController.TransferEventValid) return;

            uint ep = XhciController.TransferEventEpId;
            uint code = XhciController.TransferEventCode;
            XhciController.TransferEventValid = false;


            if (XhciController.TransferEventSlot != XhciController.ActiveSlotId) return;
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
            if (HasReportId && report[0] != ExpectedReportId)
            {
                return;
            }

            byte modifiers = report[ModifierByteOffset];
            bool shift = (modifiers & (ModLeftShift | ModRightShift)) != 0;
            s_lastModifiers = modifiers;

            fixed (byte* prev = s_state.Previous)
            {
                for (int i = KeyArrayStart; i <= KeyArrayEnd; i++)
                {
                    byte usage = report[i];
                    if (usage == 0) continue;

                    // Skip usages already held in the previous report.
                    bool alreadyHeld = false;
                    for (int j = KeyArrayStart; j <= KeyArrayEnd; j++)
                    {
                        if (prev[j] == usage) { alreadyHeld = true; break; }
                    }
                    if (alreadyHeld) continue;


                    EmitUsage(usage, shift);
                }

                int copyLimit = KeyArrayEnd + 1;
                if (copyLimit < 8) copyLimit = 8;
                if (copyLimit > 64) copyLimit = 64;
                for (int i = 0; i < copyLimit; i++) prev[i] = report[i];
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
            byte* buf = stackalloc byte[80];
            byte* p = buf;
            p = AppStr(p, prefix);
            p = AppHex(p, code);
            *p++ = (byte)'\n';
            *p = 0;
            SyscallWrappers.Log(buf);
        }
    }
}
