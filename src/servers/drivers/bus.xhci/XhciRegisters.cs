using System.Runtime.InteropServices;

namespace Xhci
{
    /// <summary>A generic 16-byte Transfer Request Block.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Trb
    {
        public ulong Parameter;
        public uint Status;
        public uint Control;
    }

    /// <summary>Event Ring Segment Table entry (16 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ErStEntry
    {
        public ulong RingBaseAddress;
        public uint RingSize;
        public uint Reserved;
    }

    /// <summary>
    /// xHCI register offsets, bit definitions and descriptor constants.
    /// Register addresses are expressed relative to their block base:
    /// capability = BAR0, operational = BAR0 + CAPLENGTH,
    /// runtime = BAR0 + RTSOFF, doorbell = BAR0 + DBOFF.
    /// </summary>
    public static unsafe class XhciRegisters
    {
        // ---- Capability registers (BAR0 + offset) ----
        public const uint CapLength = 0x00;
        public const uint HcsParams1 = 0x04; // MaxSlots[7:0], MaxIntrs[18:8], MaxPorts[31:24]
        public const uint HcsParams2 = 0x08; // MaxScratchpadBufs[25:21]
        public const uint HccParams1 = 0x10; // EECP[31:16]
        public const uint DbOff = 0x14;
        public const uint RtOff = 0x18;
        public const uint HccParams2 = 0x1C;

        // ---- Operational registers (offset from operational base) ----
        public const uint UsbCmd = 0x00;
        public const uint UsbSts = 0x04;
        public const uint PageSize = 0x08;
        public const uint CrCr = 0x18;
        public const uint Dcbaap = 0x30;
        public const uint Config = 0x38;
        public const uint PortScBase = 0x400;
        public const uint PortScStride = 0x10;

        // USBCMD bits
        public const uint CmdRs = 1u << 0;
        public const uint CmdHcReset = 1u << 1;
        public const uint CmdInte = 1u << 2;
        public const uint CmdEie = 1u << 3;
        public const uint CmdHsee = 1u << 4;

        // USBSTS bits
        public const uint StsHch = 1u << 0;
        public const uint StsHse = 1u << 2;
        public const uint StsEint = 1u << 3;
        public const uint StsPcd = 1u << 4;
        public const uint StsCnr = 1u << 11;

        // PORTSC bits
        public const uint PortCcs = 1u << 0;
        public const uint PortPed = 1u << 1;
        public const uint PortPr = 1u << 4;
        public const uint PortPp = 1u << 9;
        public const uint PortLws = 1u << 16;
        public const uint PortCsc = 1u << 17;
        public const uint PortWrc = 1u << 19;
        public const uint PortPrc = 1u << 21;
        public const uint PortChangeBits = PortCsc | PortWrc | PortPrc;

        // ---- Runtime registers: Interrupter 0 (offset from runtime base) ----
        public const uint InterrupterOffset = 0x20;
        public const uint Iman = 0x00;
        public const uint Imod = 0x04;
        public const uint ErstSz = 0x08;
        public const uint ErstBa = 0x10;
        public const uint ErdP = 0x18;

        public const uint ImanIp = 1u << 0; // Interrupt Pending (RW1C)
        public const uint ImanIe = 1u << 1; // Interrupt Enable
        public const uint ErdPEhb = 1u << 3; // Event Handler Busy (RW1C)

        // ---- Extended capabilities ----
        public const uint UsbLegSupId = 0x01;
        public const uint UsbLegCtlStsId = 0x02;
        public const uint XecpNextMask = 0xFF00;
        public const uint XecpBiosOwned = 1u << 16;
        public const uint XecpOsOwned = 1u << 24;

        // ---- TRB types ----
        public const uint TrbNormal = 1;
        public const uint TrbSetupStage = 2;
        public const uint TrbDataStage = 3;
        public const uint TrbStatusStage = 4;
        public const uint TrbLink = 6;
        public const uint TrbEnableSlot = 9;
        public const uint TrbAddressDevice = 11;
        public const uint TrbConfigureEndpoint = 12;
        public const uint TrbNoOpCmd = 23;
        public const uint TrbTransferEvent = 32;
        public const uint TrbCommandCompletion = 33;
        public const uint TrbPortStatusChange = 34;

        // TRB control/status bit fields
        public const uint TrbCycle = 1u << 0;
        public const uint TrbEnt = 1u << 1;
        public const uint TrbIsp = 1u << 2; // Interrupt on Short Packet
        public const uint TrbIoc = 1u << 5; // Interrupt on Completion
        public const uint TrbIdt = 1u << 6; // Immediate Data
        public const uint TrbBsr = 1u << 9; // Block Set Address Request
        public const uint TrbDirIn = 1u << 16;
        public const uint TrbTypeShift = 10;

        // Setup packet Transfer Type (TRT) field, bits 17:16
        public const uint TrtNoData = 0u << 16;
        public const uint TrtOut = 2u << 16;
        public const uint TrtIn = 3u << 16;

        // Completion codes
        public const uint CcSuccess = 1;
        public const uint CcTrbError = 5;
        public const uint CcStall = 6;
        public const uint CcShortPacket = 13;
        public const uint CcBandwidth = 17;
        public const uint CcContextState = 19;

        // ---- Endpoint types (Endpoint Context dword1 bits 5:3) ----
        public const uint EpTypeIsochOut = 1;
        public const uint EpTypeIsochIn = 2;
        public const uint EpTypeBulkOut = 3;
        public const uint EpTypeBulkIn = 4;
        public const uint EpTypeInterruptOut = 5;
        public const uint EpTypeInterruptIn = 6;
        // Control (bidirectional, EP0 only). Confirmed against QEMU's xHCI,
        // which accepts 3 for the default control endpoint.
        public const uint EpTypeControl = 3;

        /// <summary>EP0 uses the confirmed Control encoding unconditionally.</summary>
        public static uint Ep0TypeForAttempt(int attempt)
        {
            return EpTypeControl;
        }

        /// <summary>Interrupt IN type: spec value 6 first, implementation value 3 second.</summary>
        public static uint InterruptInTypeForAttempt(int attempt)
        {
            return attempt == 0 ? EpTypeInterruptIn : 3u;
        }

        public const int EndpointTypeAttempts = 2;

        // ---- Endpoint context dword1 fields ----
        public const uint EpInfo2CErrShift = 1;
        public const uint EpInfo2EpTypeShift = 3;
        public const uint EpInfo2MaxPacketShift = 16;

        // ---- Input control context add flags ----
        public const uint AddSlotContext = 1u << 0;
        public const uint AddEndpoint0 = 1u << 1;
        public const uint AddEndpoint1In = 1u << 3; // DCI 3

        // ---- HID / USB descriptor constants ----
        public const byte HidClass = 0x03;
        public const byte HidSubclassBoot = 0x01;
        public const byte HidProtocolKeyboard = 0x01;
        public const byte DescriptorTypeDevice = 0x01;
        public const byte DescriptorTypeConfiguration = 0x02;
        public const byte DescriptorTypeInterface = 0x04;
        public const byte DescriptorTypeEndpoint = 0x05;
        public const byte DescriptorTypeHid = 0x21;
        public const byte DescriptorTypeHidReport = 0x22;

        // bmRequestType
        public const byte BmRequestIn = 0x80;
        public const byte BmRequestTypeStandard = 0x00;
        public const byte BmRequestTypeClass = 0x20;
        public const byte BmRecipientDevice = 0x00;
        public const byte BmRecipientInterface = 0x01;

        // bRequest
        public const byte ReqGetDescriptor = 0x06;
        public const byte ReqSetConfiguration = 0x09;
        public const byte ReqSetDescriptor = 0x07;
        public const byte ReqSetIdle = 0x0A;
        public const byte ReqSetProtocol = 0x0B;

        // Endpoint attributes
        public const byte EpAttrInterrupt = 0x03;

        // ---------------- TRB bit helpers ----------------

        public static uint MakeControl(uint type, uint flags, uint cycle)
        {
            return (type << (int)TrbTypeShift) | flags | (cycle & TrbCycle);
        }

        public static uint TrbTypeOf(uint control)
        {
            return (control >> (int)TrbTypeShift) & 0x3F;
        }

        public static uint EventCompletionCode(uint status)
        {
            return (status >> 24) & 0xFF;
        }

        public static uint EventSlotId(uint control)
        {
            return (control >> 24) & 0xFF;
        }

        public static uint EventEndpointId(uint control)
        {
            return (control >> 16) & 0x1F;
        }

        public static uint EventTransferLength(uint status)
        {
            return status & 0xFFFFFF;
        }

        /// <summary>Physical address of the TRB a Transfer/Command event refers to.</summary>
        public static ulong EventTrbPointer(Trb* evt)
        {
            return evt->Parameter;
        }

        // ---------------- Port helpers ----------------

        public static uint PortSpeed(uint portSc)
        {
            return (portSc >> 10) & 0x0F;
        }

        /// <summary>
        /// EP0 default max packet size derived from the PORTSC port speed field.
        /// Full (1) and Low (2) = 8, High (3) = 64, Super (4) / SuperPlus (5) = 512.
        /// QEMU usb-kbd enumerates full-speed, so 8 is the exercised path.
        /// </summary>
        public static byte DefaultMaxPacketSizeForSpeed(uint speed)
        {
            switch (speed)
            {
                case 1: return 8;   // Full speed
                case 2: return 8;   // Low speed
                case 3: return 64;  // High speed
                case 4: return 9;   // SuperSpeed (bMaxPacketSize0 exponent: 2^9 = 512)
                case 5: return 9;   // SuperSpeedPlus
                default: return 8;
            }
        }

        /// <summary>
        /// Converts the PORTSC speed to the Slot Context speed value and the
        /// EP0 control max packet size (in bytes) used by the device context.
        /// </summary>
        public static byte MaxPacketBytesForSpeed(uint speed)
        {
            switch (speed)
            {
                case 1: return 8;    // Full speed
                case 2: return 8;    // Low speed
                case 3: return 64;   // High speed
                case 4: return 255;  // SuperSpeed: 512 reported via 2^9 exponent
                case 5: return 255;
                default: return 8;
            }
        }

        public static uint SlotSpeedForPortSpeed(uint portSpeed)
        {
            // Slot Context speed field values match the PORTSC speed values.
            return portSpeed;
        }
    }
}
