using System;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Boot;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Xhci
{
    /// <summary>
    /// xHCI controller bring-up, command ring submission and event ring parsing.
    ///
    /// Invariants:
    ///  - The USBLEGSUP ownership handshake always completes (or is explicitly
    ///    abandoned) BEFORE any USBCMD write, so the BIOS legacy PS/2 emulation
    ///    is never disabled unless the driver can take over input.
    ///  - No userland EOI is ever issued. EOI for vector 0x32 is performed by
    ///    the kernel InterruptDispatcher after it signals Notification Slot 15.
    ///  - Every ring keeps its own cycle-bit state.
    /// </summary>
    public static unsafe class XhciController
    {
        // ---- BAR0 memory map ----
        public const ulong Bar0PhysFallback = 0;
        public const ulong Bar0Virt = 0x76000000UL;
        public const ulong Bar0MapSize = 0x10000UL;

        // ---- DMA buffers (4 KiB aligned, inside the bus.xhci address space) ----
        public const ulong DcbaaVirt = 0x76100000UL;
        public const ulong CommandRingVirt = 0x76200000UL;
        public const ulong EventRingVirt = 0x76300000UL;
        public const ulong ErstVirt = 0x76400000UL;
        public const ulong ScratchpadArrayVirt = 0x76500000UL;
        public const ulong ScratchpadBufVirt = 0x76510000UL;
        public const ulong InputContextVirt = 0x76600000UL;
        public const ulong DeviceContextVirt = 0x76700000UL;
        public const ulong Ep0RingVirt = 0x76800000UL;
        public const ulong Ep1RingVirt = 0x76900000UL;
        public const ulong ControlBufferVirt = 0x76A00000UL;
        public const ulong ReportBufferVirt = 0x76B00000UL;

        public const int CommandRingEntries = 64;
        public const int EventRingEntries = 256;
        public const int Ep0RingEntries = 64;
        public const int Ep1RingEntries = 256;
        public const int TimeoutIterations = 100000;
        public const int PortResetTimeout = 500000;

        // ---- Discovered capability data ----
        public static byte* Bar0;
        public static uint OpBaseOffset;   // CAPLENGTH
        public static uint RtBaseOffset;   // RTSOFF
        public static uint DbBaseOffset;   // DBOFF
        public static uint MaxSlots;
        public static uint MaxPorts;
        public static uint MaxScratchpadBufs;
        public static ulong HccParams1Raw;

        public static bool IsInitialized;
        public static uint MsiMode; // 0 = none (poll), 1 = MSI, 2 = MSI-X
        /// <summary>Cached at init: the doorbell decoder differs under emulation.</summary>
        public static bool IsHypervisor;

        /// <summary>Context entry size: 64 when HCCPARAMS1.CSZ (bit 2) is set.</summary>
        public static uint CtxSize = 32;

        /// <summary>Control transfer timeout in loop iterations (speed dependent).</summary>
        public static int ControlTimeoutIterations = 500000;

        /// <summary>Cooperative yield loop (~50 ms equivalent) for device settle after port reset.</summary>
        public static int SettleTimeoutIterations => IsHypervisor ? 10 : 25000;

        /// <summary>Last completion code observed, for the failure dump.</summary>
        public static uint LastFailureCode;
        public static bool FailureDumped;

        /// <summary>Full memory fence between DMA writes and a doorbell kick.</summary>
        [DllImport("*")]
        public static extern void Mfence();

        // ---- Rings ----
        public static ulong* Dcbaa;
        public static ulong DcbaaPhys;
        public static Trb* CommandRing;
        public static ulong CommandRingPhys;
        public static uint CommandRingEnqueue;
        public static uint CommandRingCycle = 1;

        public static Trb* EventRing;
        public static ulong EventRingPhys;
        public static uint EventRingDequeue;
        public static uint EventRingCycle = 1;

        public static ErStEntry* Erst;
        public static ulong ErstPhys;

        public static uint* InputContext;
        public static ulong InputContextPhys;
        public static uint* DeviceContext;
        public static ulong DeviceContextPhys;

        public static Trb* Ep0Ring;
        public static ulong Ep0RingPhys;
        public static uint Ep0Enqueue;
        public static uint Ep0Cycle = 1;

        public static Trb* Ep1Ring;
        public static ulong Ep1RingPhys;
        public static uint Ep1Enqueue;
        public static uint Ep1Cycle = 1;

        public static ulong ControlBufferPhys;
        public static ulong ReportBufferPhys;

        // ---- Command completion capture ----
        public static uint LastCompletionCode;
        public static uint LastCompletionSlot;
        public static uint LastCompletionControl;
        public static uint LastCompletionStatus;
        public static ulong LastCompletionParam;
        public static bool LastCommandDone;
        public static ulong LastCommandTrbPhys;

        // ---- Port status change capture ----
        public static bool HasPendingPortChange;
        public static uint PendingPortId;

        // ---- Transfer event capture (consumed by UsbHidKeyboard) ----
        public static bool TransferEventValid;
        public static uint TransferEventEpId;
        public static uint TransferEventSlot;
        public static uint TransferEventLength;
        public static uint TransferEventCode;
        public static ulong TransferEventTrbPhys;
        public static Trb LastTransferEvent;
        public static Trb LastRawEvent;

        public static uint Ep0ControlType = XhciRegisters.EpTypeControl;
        public static uint ActiveSlotId;
        public static uint SlotId => ActiveSlotId;
        public static uint RootPortId;
        private static bool s_loggedDiscarded;

        // =====================================================================
        // Register access helpers
        // =====================================================================
        private static uint* Op(uint offset) => (uint*)(Bar0 + OpBaseOffset + offset);
        private static uint* Rt(uint offset) => (uint*)(Bar0 + RtBaseOffset + offset);
        private static uint* Db(uint offset) => (uint*)(Bar0 + DbBaseOffset + offset);

        /// <summary>
        /// xHCI 5.6: Doorbell Array @ BAR0 + DBOFF. One 32-bit register per Slot ID:
        /// Offset = DBOFF + (SlotID * 4).
        /// Value  = bits 7:0 DB Target (DCI: 1 for EP0, 3 for EP1 IN).
        /// </summary>
        public static void RingDoorbell(uint slot, uint dci)
        {
            uint offset = DbBaseOffset + (slot * 4);
            uint* db = (uint*)(Bar0Virt + offset);
            Mfence();
            *db = dci & 0xFFu;
            Mfence();
        }

        public static uint ReadOp(uint offset) => *Op(offset);
        public static void WriteOp(uint offset, uint value) => *Op(offset) = value;

        public static uint ReadPortSc(uint portId)
        {
            return *Op(XhciRegisters.PortScBase + ((portId - 1) * XhciRegisters.PortScStride));
        }

        public static void WritePortSc(uint portId, uint value)
        {
            // PED is RW1C and writing 1 disables the port, so it is masked out
            // of every write (change-bit clears and PowerPorts alike).
            value &= ~XhciRegisters.PortPed;
            *Op(XhciRegisters.PortScBase + ((portId - 1) * XhciRegisters.PortScStride)) = value;
        }

        // =====================================================================
        // Utilities
        // =====================================================================
        public static void ZeroMemory(byte* ptr, ulong length)
        {
            for (ulong i = 0; i < length; i++) ptr[i] = 0;
        }

        private static void PrintHex(ulong value)
        {
            byte* buf = stackalloc byte[19];
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

        // =====================================================================
        // Initialization
        // =====================================================================
        public static bool Initialize()
        {
            KernelBootInfo bootInfo = default;
            SyscallWrappers.GetBootInfo(&bootInfo);
            IsHypervisor = bootInfo.IsHypervisor != 0;

            var pci = new PciServiceClient(endpointCptr: 6);
            if (Bar0 == null)
            {
                ulong bar0Phys = pci.FindDeviceExact(0x0C, 0x03, 0x30);
                if (bar0Phys == 0)
                {
                    SyscallWrappers.Log("[XHCI] No xHCI controller found (class 0C/03/30).\n");
                    return false;
                }
                SyscallWrappers.MapMmio(bar0Phys, Bar0Virt, Bar0MapSize, writeCombining: false);
                Bar0 = (byte*)Bar0Virt;
            }
            MsiMode = pci.GetMsiMode(0x0C, 0x03);

            uint capLength = *(byte*)(Bar0 + XhciRegisters.CapLength);
            ulong hcs1 = *(uint*)(Bar0 + XhciRegisters.HcsParams1);
            ulong hcs2 = *(uint*)(Bar0 + XhciRegisters.HcsParams2);
            HccParams1Raw = *(uint*)(Bar0 + XhciRegisters.HccParams1);
            DbBaseOffset = *(uint*)(Bar0 + XhciRegisters.DbOff) & 0xFFFFFFFC;
            RtBaseOffset = *(uint*)(Bar0 + XhciRegisters.RtOff) & 0xFFFFFFE0;
            OpBaseOffset = capLength;

            MaxSlots = (uint)(hcs1 & 0xFF);
            MaxPorts = (uint)((hcs1 >> 24) & 0xFF);
            // Per xHCI spec HCS2[31:27] is the MSB (Hi) and HCS2[25:21] the LSB (Lo).
            MaxScratchpadBufs = (uint)((((hcs2 >> 27) & 0x1F) << 5) | ((hcs2 >> 21) & 0x1F));

            // CSZ (HCCPARAMS1 bit 2) selects 64-byte context entries.
            CtxSize = (HccParams1Raw & 0x4) != 0 ? 64u : 32u;

            // 1. Wait until the controller is ready (CNR clear).
            if (!WaitWhileSet(XhciRegisters.UsbSts, XhciRegisters.StsCnr, TimeoutIterations))
            {
                SyscallWrappers.Log("[XHCI] FAIL CNR\n");
                return false;
            }

            // 2. USBLEGSUP handshake BEFORE any USBCMD write.
            if (!LegacyHandoff())
            {
                return false;
            }

            // 3. Halt the controller, then perform a Host Controller Reset.
            //    Firmware that previously drove the controller leaves a slot
            //    bound to our root port, which makes a fresh Address Device fail
            //    with TRB Error. HCRST returns every slot and port to Default.
            uint cmd = ReadOp(XhciRegisters.UsbCmd);
            cmd &= ~XhciRegisters.CmdRs;
            WriteOp(XhciRegisters.UsbCmd, cmd);
            // HCH becomes 1 once the controller has actually halted.
            if (!WaitWhileClear(XhciRegisters.UsbSts, XhciRegisters.StsHch, TimeoutIterations))
            {
                SyscallWrappers.Log("[XHCI] FAIL HCRST\n");
                return false;
            }

            // HCRST is self-clearing: wait for it to read back as 0.
            WriteOp(XhciRegisters.UsbCmd, cmd | XhciRegisters.CmdHcReset);
            if (!WaitWhileSet(XhciRegisters.UsbCmd, XhciRegisters.CmdHcReset, TimeoutIterations))
            {
                SyscallWrappers.Log("[XHCI] FAIL HCRST\n");
                return false;
            }
            if (!WaitWhileSet(XhciRegisters.UsbSts, XhciRegisters.StsCnr, TimeoutIterations))
            {
                SyscallWrappers.Log("[XHCI] FAIL CNR\n");
                return false;
            }

            // 3b. Power every root port before it is touched. A port with PP
            //     clear is never reset, and CCS is only sampled once power has
            //     had time to settle.
            PowerPorts();

            // 4. Allocate all rings and contexts in DMA-coherent memory.
            if (!AllocateRings())
            {
                return false;
            }

            // 4b. Force the DMA buffers uncacheable and report one page's PTE
            //     flags so cache-coherence problems are visible on serial.
            {
                ulong* pages = stackalloc ulong[11];
                pages[0] = Bar0Virt;
                pages[1] = DcbaaVirt;
                pages[2] = CommandRingVirt;
                pages[3] = EventRingVirt;
                pages[4] = ErstVirt;
                pages[5] = InputContextVirt;
                pages[6] = DeviceContextVirt;
                pages[7] = Ep0RingVirt;
                pages[8] = Ep1RingVirt;
                pages[9] = ControlBufferVirt;
                pages[10] = ReportBufferVirt;

                for (int i = 0; i < 11; i++)
                {
                    SyscallWrappers.DmaCoherent(pages[i], true);
                }
            }

            // 5. Program the command ring, DCBAA, page size and interrupter.
            *(uint*)(Bar0 + OpBaseOffset + XhciRegisters.Config) = MaxSlots;
            *(ulong*)(Bar0 + OpBaseOffset + XhciRegisters.Dcbaap) = DcbaaPhys;
            *(ulong*)(Bar0 + OpBaseOffset + XhciRegisters.CrCr) = CommandRingPhys | 1UL; // RCS = 1
            WriteOp(XhciRegisters.PageSize, 1); // 4 KiB

            // Interrupter 0: one event ring segment, IE enabled.
            *Rt(XhciRegisters.InterrupterOffset + XhciRegisters.ErstSz) = 1;
            *(ulong*)(Bar0 + RtBaseOffset + XhciRegisters.InterrupterOffset + XhciRegisters.ErstBa) = ErstPhys;
            *(ulong*)(Bar0 + RtBaseOffset + XhciRegisters.InterrupterOffset + XhciRegisters.ErdP) = EventRingPhys;
            *Rt(XhciRegisters.InterrupterOffset + XhciRegisters.Iman) = XhciRegisters.ImanIe;

            // 6. Run. This is the first point at which legacy emulation may drop.
            uint runCmd = XhciRegisters.CmdInte | XhciRegisters.CmdEie | XhciRegisters.CmdRs;
            WriteOp(XhciRegisters.UsbCmd, runCmd);
            if (!WaitWhileSet(XhciRegisters.UsbSts, XhciRegisters.StsHch, TimeoutIterations))
            {
                SyscallWrappers.Log("[XHCI] FAIL RUN\n");
                return false;
            }

            IsInitialized = true;
            SyscallWrappers.Log("[XHCI] Host controller initialized.\n");
            return true;
        }

        private static bool WaitWhileSet(uint statusOffset, uint mask, int iterations)
        {
            int spin = iterations;
            while (spin-- > 0)
            {
                if ((ReadOp(statusOffset) & mask) == 0) return true;
                SyscallWrappers.Yield();
            }
            return false;
        }

        /// <summary>Waits until all bits in mask are CLEAR in the register.</summary>
        private static bool WaitWhileClear(uint regOffset, uint mask, int iterations)
        {
            int spin = iterations;
            while (spin-- > 0)
            {
                if ((ReadOp(regOffset) & mask) != 0) return true;
                SyscallWrappers.Yield();
            }
            return false;
        }

        /// <summary>
        /// USBLEGSUP handshake. Absent capability is success (nothing to hand
        /// off). Present capability plus timeout is a failure during which OS
        /// ownership is released so the BIOS keeps providing legacy emulation.
        /// </summary>
        private static bool LegacyHandoff()
        {
            uint eecp = (uint)((HccParams1Raw >> 16) & 0xFFFF);
            if (eecp == 0)
            {
                return true;
            }

            byte* xecp = Bar0 + (eecp << 2);

            int guard = 64;
            while (guard-- > 0)
            {
                uint cap = *(uint*)xecp;
                uint capId = cap & 0xFF;

                if (capId == XhciRegisters.UsbLegSupId)
                {
                    if ((cap & XhciRegisters.XecpBiosOwned) == 0)
                    {
                        return true;
                    }

                    // Claim ownership WITHOUT touching USBLEGCTLSTS: the BIOS may
                    // still be poking the controller while it releases ownership.
                    *(uint*)xecp = cap | XhciRegisters.XecpOsOwned;

                    bool released = false;
                    int spin = 1000000; // ~2 x 500 ms of cooperative yielding
                    while (spin-- > 0)
                    {
                        if ((*(uint*)xecp & XhciRegisters.XecpBiosOwned) == 0)
                        {
                            released = true;
                            break;
                        }
                        SyscallWrappers.Yield();
                    }

                    if (released)
                    {
                        // Only now silence the BIOS SMI sources.
                        *(uint*)(xecp + 4) = 0;
                        return true;
                    }

                    // Clear the OS-owned bit we set and abort so the BIOS keeps
                    // providing legacy PS/2 emulation.
                    *(uint*)xecp = *(uint*)xecp & ~XhciRegisters.XecpOsOwned;
                    SyscallWrappers.Log("[XHCI] handoff aborted; BIOS keeps legacy emulation\n");
                    return false;
                }

                uint next = (cap & XhciRegisters.XecpNextMask) >> 8;
                if (next == 0) break;
                xecp += (next << 2);
            }

            return true;
        }

        private static bool AllocateRings()
        {
            // DCBAA: MaxSlots + 1 entries (entry 0 used for the scratchpad array).
            int dcbaaEntries = (int)MaxSlots + 1;
            ulong dcbaaBytes = (ulong)(dcbaaEntries * 8);
            DcbaaPhys = SyscallWrappers.AllocDma(4096, DcbaaVirt);
            if (DcbaaPhys == 0) { SyscallWrappers.Log("[XHCI] DCBAA allocation failed.\n"); return false; }
            Dcbaa = (ulong*)DcbaaVirt;
            ZeroMemory((byte*)Dcbaa, 4096);

            // Scratchpad buffers (QEMU reports 0, so this branch is normally
            // skipped, but the path is kept for real hardware).
            if (MaxScratchpadBufs > 0)
            {
                ulong arrPhys = SyscallWrappers.AllocDma(4096, ScratchpadArrayVirt);
                if (arrPhys == 0) { SyscallWrappers.Log("[XHCI] Scratchpad array allocation failed.\n"); return false; }
                ulong* arr = (ulong*)ScratchpadArrayVirt;
                ZeroMemory((byte*)arr, 4096);

                for (uint i = 0; i < MaxScratchpadBufs; i++)
                {
                    ulong bufVirt = ScratchpadBufVirt + ((ulong)i * 4096UL);
                    ulong bufPhys = SyscallWrappers.AllocDma(4096, bufVirt);
                    if (bufPhys == 0) { SyscallWrappers.Log("[XHCI] Scratchpad buffer allocation failed.\n"); return false; }
                    ZeroMemory((byte*)bufVirt, 4096);
                    arr[i] = bufPhys;
                }
                Dcbaa[0] = arrPhys;
                SyscallWrappers.Log("[XHCI] Scratchpad buffers provisioned.\n");
            }
            else
            {
                Dcbaa[0] = 0;
            }

            // Command ring.
            CommandRingPhys = SyscallWrappers.AllocDma(4096, CommandRingVirt);
            if (CommandRingPhys == 0) { SyscallWrappers.Log("[XHCI] Command ring allocation failed.\n"); return false; }
            CommandRing = (Trb*)CommandRingVirt;
            ZeroMemory((byte*)CommandRing, 4096);
            CommandRingEnqueue = 0;
            CommandRingCycle = 1;
            RefreshLinkTrb(CommandRing, CommandRingEntries, CommandRingPhys, CommandRingCycle);

            // Event ring + ERST.
            EventRingPhys = SyscallWrappers.AllocDma(4096, EventRingVirt);
            if (EventRingPhys == 0) { SyscallWrappers.Log("[XHCI] Event ring allocation failed.\n"); return false; }
            EventRing = (Trb*)EventRingVirt;
            ZeroMemory((byte*)EventRing, 4096);
            EventRingDequeue = 0;
            EventRingCycle = 1;

            ErstPhys = SyscallWrappers.AllocDma(4096, ErstVirt);
            if (ErstPhys == 0) { SyscallWrappers.Log("[XHCI] ERST allocation failed.\n"); return false; }
            Erst = (ErStEntry*)ErstVirt;
            ZeroMemory((byte*)Erst, 4096);
            Erst[0].RingBaseAddress = EventRingPhys;
            Erst[0].RingSize = EventRingEntries;
            Erst[0].Reserved = 0;

            // Contexts and transfer rings.
            InputContextPhys = SyscallWrappers.AllocDma(4096, InputContextVirt);
            if (InputContextPhys == 0) return false;
            InputContext = (uint*)InputContextVirt;
            ZeroMemory((byte*)InputContext, 4096);

            DeviceContextPhys = SyscallWrappers.AllocDma(4096, DeviceContextVirt);
            if (DeviceContextPhys == 0) return false;
            DeviceContext = (uint*)DeviceContextVirt;
            ZeroMemory((byte*)DeviceContext, 4096);

            Ep0RingPhys = SyscallWrappers.AllocDma(4096, Ep0RingVirt);
            if (Ep0RingPhys == 0) return false;
            Ep0Ring = (Trb*)Ep0RingVirt;
            ZeroMemory((byte*)Ep0Ring, 4096);
            Ep0Enqueue = 0;
            Ep0Cycle = 1;
            RefreshLinkTrb(Ep0Ring, Ep0RingEntries, Ep0RingPhys, Ep0Cycle);

            Ep1RingPhys = SyscallWrappers.AllocDma(4096, Ep1RingVirt);
            if (Ep1RingPhys == 0) return false;
            Ep1Ring = (Trb*)Ep1RingVirt;
            ZeroMemory((byte*)Ep1Ring, 4096);
            Ep1Enqueue = 0;
            Ep1Cycle = 1;
            RefreshLinkTrb(Ep1Ring, Ep1RingEntries, Ep1RingPhys, Ep1Cycle);

            ControlBufferPhys = SyscallWrappers.AllocDma(4096, ControlBufferVirt);
            ReportBufferPhys = SyscallWrappers.AllocDma(4096, ReportBufferVirt);
            ZeroMemory((byte*)ControlBufferVirt, 4096);
            ZeroMemory((byte*)ReportBufferVirt, 4096);

            // 64-byte alignment assertions (DMA frames are 4096 aligned, so
            // this is a guarantee check rather than a corrective step).
            if ((DcbaaPhys & 0x3F) != 0 || (CommandRingPhys & 0x3F) != 0 || (ErstPhys & 0x3F) != 0)
            {
                SyscallWrappers.Log("[XHCI] Ring alignment violation; aborting.\n");
                return false;
            }

            SyscallWrappers.Log("[XHCI] DCBAA, Command Ring, Event Ring and ERST allocated and aligned.\n");
            return true;
        }

        private static void RefreshLinkTrb(Trb* ring, int entries, ulong ringPhys, uint cycle)
        {
            Trb* link = ring + (entries - 1);
            link->Parameter = ringPhys;
            link->Status = 0;
            link->Control = (XhciRegisters.TrbLink << (int)XhciRegisters.TrbTypeShift) | 0x2u | (cycle & 1u);
        }

        // =====================================================================
        // Event ring
        // =====================================================================
        /// <summary>
        /// Pops one event TRB. On success the Event Ring Dequeue Pointer is
        /// advanced and ERDP is written with the EHB bit to rearm the
        /// interrupter. No EOI is issued here by design.
        /// </summary>
        public static bool TryDequeueEvent(out Trb evt)
        {
            evt = default;
            if (EventRing == null) return false;

            Trb* slot = EventRing + EventRingDequeue;
            if ((slot->Control & XhciRegisters.TrbCycle) != EventRingCycle) return false;

            evt = *slot;
            EventRingDequeue++;
            if (EventRingDequeue >= EventRingEntries)
            {
                EventRingDequeue = 0;
                EventRingCycle ^= 1;
            }

            ulong erdp = EventRingPhys + ((ulong)EventRingDequeue * 16UL);
            *(ulong*)(Bar0 + RtBaseOffset + XhciRegisters.InterrupterOffset + XhciRegisters.ErdP) = erdp | XhciRegisters.ErdPEhb;
            return true;
        }

        /// <summary>Drains and classifies all currently available event TRBs.</summary>
        public static void ProcessEvents()
        {
            Trb evt;
            while (TryDequeueEvent(out evt))
            {
                LastRawEvent = evt;
                uint type = XhciRegisters.TrbTypeOf(evt.Control);
                if (type == XhciRegisters.TrbPortStatusChange)
                {
                    HasPendingPortChange = true;
                    // Port Status Change Event: Port ID is in dword0 bits 31:24.
                    PendingPortId = (uint)((evt.Parameter >> 24) & 0xFFUL);
                }
                else if (type == XhciRegisters.TrbCommandCompletion)
                {
                    LastCompletionCode = XhciRegisters.EventCompletionCode(evt.Status);
                    LastCompletionSlot = XhciRegisters.EventSlotId(evt.Control);
                    LastCompletionControl = evt.Control;
                    LastCompletionStatus = evt.Status;
                    LastCompletionParam = evt.Parameter;
                    LastCommandTrbPhys = evt.Parameter;
                    LastCommandDone = true;
                    break;
                }
                else if (type == XhciRegisters.TrbTransferEvent)
                {
                    LastTransferEvent = evt;
                    TransferEventValid = true;
                    TransferEventEpId = XhciRegisters.EventEndpointId(evt.Control);
                    TransferEventSlot = XhciRegisters.EventSlotId(evt.Control);
                    TransferEventLength = XhciRegisters.EventTransferLength(evt.Status);
                    TransferEventCode = XhciRegisters.EventCompletionCode(evt.Status);
                    TransferEventTrbPhys = evt.Parameter;
                    break;
                }
            }
        }

        // =====================================================================
        // Command ring
        // =====================================================================
        /// <summary>
        /// Enqueues a command TRB, rings the host doorbell and waits for the
        /// matching Command Completion event. Returns the completion code, or
        /// 0 on timeout.
        /// </summary>
        public static uint IssueCommand(Trb* template)
        {
            LastCommandDone = false;
            uint type = XhciRegisters.TrbTypeOf(template->Control);

            Trb* dest = CommandRing + CommandRingEnqueue;
            *dest = *template;
            dest->Control = (dest->Control & ~XhciRegisters.TrbCycle) | (CommandRingCycle & 1u);
            ulong commandPhys = CommandRingPhys + ((ulong)CommandRingEnqueue * 16UL);

            CommandRingEnqueue++;
            if (CommandRingEnqueue >= (uint)(CommandRingEntries - 1))
            {
                RefreshLinkTrb(CommandRing, CommandRingEntries, CommandRingPhys, CommandRingCycle);
                CommandRingEnqueue = 0;
                CommandRingCycle ^= 1;
            }

            Mfence();
            *Db(0) = 0; // Doorbell 0: host controller command ring

            byte* sb = stackalloc byte[128];
            int spin = TimeoutIterations;
            while (spin-- > 0)
            {
                ProcessEvents();
                if (LastCommandDone)
                {
                    LastCommandDone = false;
                    if (LastCommandTrbPhys == commandPhys)
                    {
                        return LastCompletionCode;
                    }

                    byte* sp = sb;
                    sp = AppendStr(sp, "[XHCI] stray cmd event cc=");
                    sp = AppendHex(sp, LastCompletionCode);
                    sp = AppendStr(sp, " ptr=");
                    sp = AppendHex(sp, LastCommandTrbPhys);
                    sp = AppendStr(sp, " want=");
                    sp = AppendHex(sp, commandPhys);
                    *sp++ = (byte)'\n';
                    *sp = 0;
                    SyscallWrappers.Log(sb);
                }
                SyscallWrappers.Yield();
            }

            byte* tb = stackalloc byte[64];
            byte* tp = tb;
            tp = AppendStr(tp, "[XHCI] Command timeout for TRB type ");
            tp = AppendHex(tp, type);
            *tp++ = (byte)'\n';
            *tp = 0;
            SyscallWrappers.Log(tb);
            return 0;
        }

        public static uint IssueNoOp()
        {
            Trb trb;
            trb.Parameter = 0;
            trb.Status = 0;
            trb.Control = (XhciRegisters.TrbNoOpCmd << (int)XhciRegisters.TrbTypeShift);
            return IssueCommand(&trb);
        }

        // =====================================================================
        // Event ring polling primitive
        // =====================================================================
        public static bool WaitForPortStatusChange(int iterations, out uint portId)
        {
            portId = 0;
            int spin = iterations;
            while (spin-- > 0)
            {
                ProcessEvents();
                if (HasPendingPortChange)
                {
                    HasPendingPortChange = false;
                    portId = PendingPortId;
                    return true;
                }
                SyscallWrappers.Yield();
            }
            return false;
        }

        /// <summary>Finds the first port with a connected device and clear change bits.</summary>
        public static uint FindConnectedPort()
        {
            for (uint p = 1; p <= MaxPorts; p++)
            {
                uint portSc = ReadPortSc(p);
                if ((portSc & XhciRegisters.PortCcs) != 0)
                {
                    // Clear any stale change bits.
                    WritePortSc(p, portSc | XhciRegisters.PortChangeBits);
                    return p;
                }
            }
            return 0;
        }

        /// <summary>Powers every root port and lets power settle before sampling.</summary>
        public static void PowerPorts()
        {
            for (uint p = 1; p <= MaxPorts; p++)
            {
                uint ps = ReadPortSc(p);
                if ((ps & XhciRegisters.PortPp) == 0)
                {
                    WritePortSc(p, ps | XhciRegisters.PortPp);
                }
            }
            // ~20 ms of cooperative yielding for power-up and connect detect.
            for (int i = 0; i < 200000; i++) SyscallWrappers.Yield();
        }

        /// <summary>Bounded wait for a port to report a connected device.</summary>
        public static bool WaitPortConnected(uint portId, int iterations)
        {
            int spin = iterations;
            while (spin-- > 0)
            {
                if ((ReadPortSc(portId) & XhciRegisters.PortCcs) != 0) return true;
                SyscallWrappers.Yield();
            }
            return false;
        }

        /// <summary>True while the recorded root port still reports a device.</summary>
        public static bool PortStillConnected()
        {
            if (RootPortId == 0 || RootPortId > MaxPorts) return false;
            return (ReadPortSc(RootPortId) & XhciRegisters.PortCcs) != 0;
        }

        public static bool ResetPort(uint portId)
        {
            uint portSc = ReadPortSc(portId);
            // A port that is not powered must never be reset.
            if ((portSc & XhciRegisters.PortPp) == 0) return false;
            if ((portSc & XhciRegisters.PortCcs) == 0) return false;

            // Assert Port Reset, preserving PP and clear-on-write change bits.
            WritePortSc(portId, (portSc & ~XhciRegisters.PortChangeBits) | XhciRegisters.PortPr);

            int spin = PortResetTimeout;
            while (spin-- > 0)
            {
                uint ps = ReadPortSc(portId);
                if ((ps & XhciRegisters.PortPr) == 0)
                {
                    // Reset complete: clear PRC and CSC change bits.
                    WritePortSc(portId, ps | XhciRegisters.PortPrc | XhciRegisters.PortCsc);
                    uint postResetPs = ReadPortSc(portId);
                    uint speed = (postResetPs >> 10) & 0x0F;

                    // SuperSpeed (USB 3.0, speed >= 4) requires link transition to U0
                    // via LWS and PLS=0. USB 2.0 (speed 1, 2, 3) enables automatically;
                    // attempting LWS on USB 2.0 stalls real silicon.
                    if (speed >= 4)
                    {
                        uint u0Ps = (postResetPs & ~XhciRegisters.PortChangeBits & ~(0x0Fu << 5)) | XhciRegisters.PortLws;
                        WritePortSc(portId, u0Ps);
                        postResetPs = ReadPortSc(portId);
                    }
                    else
                    {
                        if ((postResetPs & XhciRegisters.PortPed) == 0)
                        {
                            int pedSpin = 10000;
                            while (pedSpin-- > 0 && (postResetPs & XhciRegisters.PortPed) == 0)
                            {
                                SyscallWrappers.Yield();
                                postResetPs = ReadPortSc(portId);
                            }
                            if ((postResetPs & XhciRegisters.PortPed) == 0)
                            {
                                return false;
                            }
                        }
                    }

                    WritePortSc(portId, ps | XhciRegisters.PortChangeBits);
                    return true;
                }
                SyscallWrappers.Yield();
            }

            SyscallWrappers.Log("[XHCI] Port reset timed out.\n");
            return false;
        }

        public static bool WaitPortEnabled(uint portId, int iterations)
        {
            int spin = iterations;
            while (spin-- > 0)
            {
                uint ps = ReadPortSc(portId);
                if ((ps & XhciRegisters.PortPed) != 0 && (ps & XhciRegisters.PortPr) == 0)
                {
                    WritePortSc(portId, ps | XhciRegisters.PortChangeBits);
                    return true;
                }
                SyscallWrappers.Yield();
            }

            uint finalPs = ReadPortSc(portId);
            if ((finalPs & XhciRegisters.PortCcs) == 0)
            {
                SyscallWrappers.Log("[XHCI] port disconnected during reset; skipping\n");
                return false;
            }

            byte* b = stackalloc byte[64];
            byte* p = b;
            p = AppendStr(p, "[XHCI] Port did not reach Enabled state, portsc=");
            p = AppendHex(p, finalPs);
            *p++ = (byte)'\n';
            *p = 0;
            SyscallWrappers.Log(b);
            return false;
        }

        // =====================================================================
        // Device context helpers
        // =====================================================================
        public static void DumpOutputContext(uint slotId = 0)
        {
        }

        public static void SetSlotContext(uint* ctx, uint speed, uint portId, uint contextEntries)
        {
            // DW0: Route String[19:0], Speed[23:20], MTT[25], Hub[26], Context Entries[31:27].
            ctx[0] = (speed << 20) | ((contextEntries & 0x1F) << 27);
            // DW1: Max Exit Latency[15:0], Root Hub Port Number[23:16],
            // Number of Ports[31:24]. Number of Ports MUST be 0 for a non-hub:
            // a consumer that reads this dword as (dw1 >> 16) would otherwise
            // fold the port into a bogus value and reject Address Device with a
            // TRB Error and no Host System Error.
            ctx[1] = (portId & 0xFF) << 16;
            ctx[2] = 0;
            ctx[3] = 0;
        }

        public static void SetEndpointContext(uint* epCtx, uint epType, ushort maxPacketSize, byte interval, ulong ringPhys)
        {
            // dword0: interval [23:16]
            epCtx[0] = ((uint)interval & 0xFF) << 16;
            // dword1: CErr = 3 (bits 2:1), EP Type (bits 5:3), MaxPacketSize (31:16)
            epCtx[1] = (3u << (int)XhciRegisters.EpInfo2CErrShift)
                     | ((epType & 0x7u) << (int)XhciRegisters.EpInfo2EpTypeShift)
                     | ((uint)maxPacketSize << (int)XhciRegisters.EpInfo2MaxPacketShift);
            // dword2/3: TR Dequeue Pointer with DCS = 1 (low 4 bits reserved)
            epCtx[2] = (uint)(ringPhys & 0xFFFFFFF0UL) | 1u;
            epCtx[3] = (uint)(ringPhys >> 32);
        }

        public static void ClearInputContextAddFlags()
        {
            // The Input Control Context is exactly CtxSize bytes wide.
            uint dwords = CtxSize / 4;
            for (uint i = 0; i < dwords; i++) InputContext[i] = 0;
        }

        public static void SetInputAddFlags(uint flags)
        {
            InputContext[1] = flags; // dword 1 = Add Context flags
        }

        // Context entries are CtxSize bytes each: Input Control Context at 0,
        // Slot Context at CtxSize, endpoint DCI d at (1 + d) * CtxSize.
        public static uint* InputSlotContext() => (uint*)((byte*)InputContext + CtxSize);
        public static uint* InputEndpoint0() => (uint*)((byte*)InputContext + (2 * CtxSize));
        public static uint* InputEndpoint(uint dci) => (uint*)((byte*)InputContext + ((1 + dci) * CtxSize));

        public static uint DisableSlot(uint slotId)
        {
            if (slotId == 0) slotId = ActiveSlotId;
            if (slotId == 0 || slotId > MaxSlots)
            {
                ActiveSlotId = 0;
                return XhciRegisters.CcSuccess;
            }
            Trb cmd;
            cmd.Parameter = 0;
            cmd.Status = 0;
            cmd.Control = (XhciRegisters.TrbDisableSlot << (int)XhciRegisters.TrbTypeShift)
                        | (slotId << 24);
            uint code = IssueCommand(&cmd);
            if (slotId <= MaxSlots && Dcbaa != null)
            {
                Dcbaa[slotId] = 0;
            }
            ActiveSlotId = 0;
            return code;
        }

        public static uint EnableSlot(out uint newSlotId)
        {
            newSlotId = 0;
            Trb enableSlot;
            enableSlot.Parameter = 0;
            enableSlot.Status = 0;
            enableSlot.Control = XhciRegisters.TrbEnableSlot << (int)XhciRegisters.TrbTypeShift;
            uint slotCode = IssueCommand(&enableSlot);
            if (slotCode == XhciRegisters.CcSuccess)
            {
                newSlotId = LastCompletionSlot;
                ActiveSlotId = LastCompletionSlot;
            }
            return slotCode;
        }

        public static void RebuildSlotState(uint slotId = 0)
        {
            if (DeviceContext != null)
            {
                ZeroMemory((byte*)DeviceContext, 4096);
            }
            if (InputContext != null)
            {
                ZeroMemory((byte*)InputContext, 4096);
            }
            if (ActiveSlotId >= 1 && ActiveSlotId <= MaxSlots && Dcbaa != null)
            {
                Dcbaa[ActiveSlotId] = DeviceContextPhys;
            }
            if (Ep0Ring != null)
            {
                ZeroMemory((byte*)Ep0Ring, 4096);
                Ep0Enqueue = 0;
                Ep0Cycle = 1;
                RefreshLinkTrb(Ep0Ring, Ep0RingEntries, Ep0RingPhys, Ep0Cycle);
            }
            if (Ep1Ring != null)
            {
                ZeroMemory((byte*)Ep1Ring, 4096);
                Ep1Enqueue = 0;
                Ep1Cycle = 1;
                RefreshLinkTrb(Ep1Ring, Ep1RingEntries, Ep1RingPhys, Ep1Cycle);
            }
        }

        public static uint EvaluateContextEp0(ushort newMps)
        {
            ClearInputContextAddFlags();
            SetInputAddFlags(XhciRegisters.AddEndpoint0);

            uint* ep0 = InputEndpoint0();
            ep0[1] = (3u << (int)XhciRegisters.EpInfo2CErrShift)
                   | ((Ep0ControlType & 0x7u) << (int)XhciRegisters.EpInfo2EpTypeShift)
                   | ((uint)newMps << (int)XhciRegisters.EpInfo2MaxPacketShift);

            Trb cmd;
            cmd.Parameter = InputContextPhys;
            cmd.Status = 0;
            cmd.Control = (XhciRegisters.TrbEvaluateContext << (int)XhciRegisters.TrbTypeShift)
                        | (ActiveSlotId << 24);
            return IssueCommand(&cmd);
        }

        /// <summary>One-shot dump of the state needed to bisect a hardware failure.</summary>
        public static void DumpFailure(uint completionCode)
        {
            if (FailureDumped) return;
            FailureDumped = true;
            LastFailureCode = completionCode;

            byte* b = stackalloc byte[320];
            byte* p = b;
            p = AppendStr(p, "[XHCI] DUMP slot=");
            p = AppendDec(p, ActiveSlotId);
            p = AppendStr(p, " portsc=");
            p = AppendHex(p, (RootPortId >= 1 && RootPortId <= MaxPorts) ? ReadPortSc(RootPortId) : 0);
            p = AppendStr(p, " usbsts=");
            p = AppendHex(p, ReadOp(XhciRegisters.UsbSts));
            p = AppendStr(p, " slot0=");
            p = AppendHex(p, InputContext != null ? InputSlotContext()[0] : 0);
            p = AppendStr(p, " slot1=");
            p = AppendHex(p, InputContext != null ? InputSlotContext()[1] : 0);
            p = AppendStr(p, " ep0_0=");
            p = AppendHex(p, InputContext != null ? InputEndpoint0()[0] : 0);
            p = AppendStr(p, " ep0_1=");
            p = AppendHex(p, InputContext != null ? InputEndpoint0()[1] : 0);
            p = AppendStr(p, " ep0_2=");
            p = AppendHex(p, InputContext != null ? InputEndpoint0()[2] : 0);
            p = AppendStr(p, " ep0_3=");
            p = AppendHex(p, InputContext != null ? InputEndpoint0()[3] : 0);
            p = AppendStr(p, " cc=");
            p = AppendHex(p, completionCode);
            *p++ = (byte)'\n';
            *p = 0;
            SyscallWrappers.Log(b);
        }

        public static void ResetFailureDump()
        {
            FailureDumped = false;
        }

        private static byte* AppendStr(byte* p, string s)
        {
            for (int i = 0; i < s.Length; i++) p[i] = (byte)s[i];
            return p + s.Length;
        }

        private static byte* AppendHex(byte* p, ulong v)
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

        // =====================================================================
        // Control transfers on EP0
        // =====================================================================
        private static void EnqueueEp0(Trb* trb)
        {
            Trb* dest = Ep0Ring + Ep0Enqueue;
            *dest = *trb;
            dest->Control = (dest->Control & ~XhciRegisters.TrbCycle) | (Ep0Cycle & 1u);
            Ep0Enqueue++;
            if (Ep0Enqueue >= (uint)(Ep0RingEntries - 1))
            {
                RefreshLinkTrb(Ep0Ring, Ep0RingEntries, Ep0RingPhys, Ep0Cycle);
                Ep0Enqueue = 0;
                Ep0Cycle ^= 1;
            }
        }

        /// <summary>
        /// Issues a control transfer on EP0 and waits for the transfer event.
        /// Returns true only when the completion code indicates success or a
        /// short packet.
        /// </summary>
        public static bool ControlTransfer(byte bmRequestType, byte bRequest, ushort wValue,
            ushort wIndex, ushort wLength, ulong bufferVirt, ulong bufferPhys,
            out uint grantedLength, int timeoutIterations = -1, bool silent = false, uint* outCompletionCode = null)
        {
            grantedLength = 0;
            TransferEventValid = false;
            LastTransferEvent = default;
            LastRawEvent = default;
            if (outCompletionCode != null) *outCompletionCode = 0;

            ulong setup = (ulong)bmRequestType
                        | ((ulong)bRequest << 8)
                        | ((ulong)wValue << 16)
                        | ((ulong)wIndex << 32)
                        | ((ulong)wLength << 48);

            bool isIn = (bmRequestType & XhciRegisters.BmRequestIn) != 0;

            // Setup Stage (IDT = 1)
            Trb setupTrb;
            setupTrb.Parameter = setup;
            setupTrb.Status = 8;

            // TRT must be "No Data" (0) whenever there is no Data Stage; using
            // OUT here is rejected with a TRB Error by real host controllers for
            // the wLength == 0 requests (SET_CONFIGURATION / SET_PROTOCOL /
            // SET_IDLE), even though QEMU tolerates it.
            uint trt = wLength == 0
                ? XhciRegisters.TrtNoData
                : (isIn ? XhciRegisters.TrtIn : XhciRegisters.TrtOut);

            setupTrb.Control = (XhciRegisters.TrbSetupStage << (int)XhciRegisters.TrbTypeShift)
                             | XhciRegisters.TrbIdt
                             | trt;
            EnqueueEp0(&setupTrb);

            // Data Stage
            if (wLength > 0)
            {
                Trb dataTrb;
                dataTrb.Parameter = bufferPhys;
                dataTrb.Status = wLength;
                dataTrb.Control = (XhciRegisters.TrbDataStage << (int)XhciRegisters.TrbTypeShift)
                                | (isIn ? XhciRegisters.TrbDirIn : 0u);
                EnqueueEp0(&dataTrb);
            }

            // Status Stage
            Trb statusTrb;
            statusTrb.Parameter = 0;
            statusTrb.Status = 0;
            statusTrb.Control = (XhciRegisters.TrbStatusStage << (int)XhciRegisters.TrbTypeShift)
                              | XhciRegisters.TrbIoc
                              | (isIn ? 0u : XhciRegisters.TrbDirIn);
            EnqueueEp0(&statusTrb);

            // Kick the default control endpoint (DCI 1).
            RingDoorbell(ActiveSlotId, 1);

            int spin = (timeoutIterations > 0) ? timeoutIterations : ControlTimeoutIterations;
            uint discardedInThisTransfer = 0;
            while (spin-- > 0)
            {
                ProcessEvents();
                if (TransferEventValid)
                {
                    uint evtSlot = (LastTransferEvent.Control >> 24) & 0xFFu;
                    uint evtDci = (LastTransferEvent.Control >> 16) & 0xFFu;

                    if (evtSlot != ActiveSlotId || evtDci != 1)
                    {
                        TransferEventValid = false;
                        discardedInThisTransfer++;
                        SyscallWrappers.Yield();
                        continue;
                    }

                    // Matching transfer event on ActiveSlotId and DCI 1
                    uint code = TransferEventCode;
                    TransferEventValid = false;

                    if (discardedInThisTransfer > 0 && !s_loggedDiscarded)
                    {
                        s_loggedDiscarded = true;
                        byte* db = stackalloc byte[64];
                        byte* dp = db;
                        dp = AppendStr(dp, "[XHCI] discarded ");
                        dp = AppendDec(dp, discardedInThisTransfer);
                        dp = AppendStr(dp, " foreign events\n");
                        *dp = 0;
                        SyscallWrappers.Log(db);
                    }

                    if (outCompletionCode != null) *outCompletionCode = code;
                    if (code == XhciRegisters.CcSuccess || code == XhciRegisters.CcShortPacket)
                    {
                        grantedLength = wLength - TransferEventLength;
                        if (grantedLength > wLength) grantedLength = wLength;
                        return true;
                    }
                    if (!silent)
                    {
                        byte* b = stackalloc byte[64];
                        byte* p = b;
                        p = AppendStr(p, "[XHCI] Control transfer failed, completion code ");
                        p = AppendHex(p, code);
                        *p++ = (byte)'\n';
                        *p = 0;
                        SyscallWrappers.Log(b);
                    }
                    return false;
                }
                SyscallWrappers.Yield();
            }

            ProcessEvents();
            if (TransferEventValid)
            {
                uint evtSlot = (LastTransferEvent.Control >> 24) & 0xFFu;
                uint evtDci = (LastTransferEvent.Control >> 16) & 0xFFu;
                if (evtSlot != ActiveSlotId || evtDci != 1)
                {
                    TransferEventValid = false;
                    discardedInThisTransfer++;
                }
                else
                {
                    uint code = TransferEventCode;
                    TransferEventValid = false;
                    if (discardedInThisTransfer > 0 && !s_loggedDiscarded)
                    {
                        s_loggedDiscarded = true;
                        byte* db = stackalloc byte[64];
                        byte* dp = db;
                        dp = AppendStr(dp, "[XHCI] discarded ");
                        dp = AppendDec(dp, discardedInThisTransfer);
                        dp = AppendStr(dp, " foreign events\n");
                        *dp = 0;
                        SyscallWrappers.Log(db);
                    }
                    if (outCompletionCode != null) *outCompletionCode = code;
                    if (code == XhciRegisters.CcSuccess || code == XhciRegisters.CcShortPacket)
                    {
                        grantedLength = wLength - TransferEventLength;
                        if (grantedLength > wLength) grantedLength = wLength;
                        return true;
                    }
                    return false;
                }
            }

            if (discardedInThisTransfer > 0)
            {
                if ((LastTransferEvent.Control >> 24) != ActiveSlotId)
                {
                    LastTransferEvent = default;
                }

                if (!s_loggedDiscarded)
                {
                    s_loggedDiscarded = true;
                    byte* db = stackalloc byte[64];
                    byte* dp = db;
                    dp = AppendStr(dp, "[XHCI] discarded ");
                    dp = AppendDec(dp, discardedInThisTransfer);
                    dp = AppendStr(dp, " foreign events\n");
                    *dp = 0;
                    SyscallWrappers.Log(db);
                }
            }

            if (outCompletionCode != null) *outCompletionCode = 0;
            if (!silent)
            {
                SyscallWrappers.Log("[XHCI] Control transfer timed out.\n");
            }
            return false;
        }

        // =====================================================================
        // Interrupt IN transfers on EP1
        // =====================================================================
        private static void EnqueueEp1(Trb* trb)
        {
            Trb* dest = Ep1Ring + Ep1Enqueue;
            *dest = *trb;
            dest->Control = (dest->Control & ~XhciRegisters.TrbCycle) | (Ep1Cycle & 1u);
            Ep1Enqueue++;
            if (Ep1Enqueue >= (uint)(Ep1RingEntries - 1))
            {
                // Refresh Link TRB with CURRENT cycle so the controller executes it and toggles cycle
                RefreshLinkTrb(Ep1Ring, Ep1RingEntries, Ep1RingPhys, Ep1Cycle);
                Ep1Enqueue = 0;
                Ep1Cycle ^= 1;
            }
        }

        public static void SubmitInterruptIn(ulong bufferPhys, ushort length)
        {
            Trb trb;
            trb.Parameter = bufferPhys;
            trb.Status = length;
            trb.Control = (XhciRegisters.TrbNormal << (int)XhciRegisters.TrbTypeShift)
                        | XhciRegisters.TrbIoc
                        | XhciRegisters.TrbIsp;
            EnqueueEp1(&trb);
            // Kick EP1 IN (DCI 3).
            RingDoorbell(ActiveSlotId, 3);
        }
    }
}
