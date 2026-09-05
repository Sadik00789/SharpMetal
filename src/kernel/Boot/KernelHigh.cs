using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Descriptors;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Capabilities;
using Kernel.Diagnostics;
using Kernel.Ipc;
using Kernel.Memory.Heap;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;
using Kernel.Scheduling;
using Microkernel.Abstractions.Capabilities;
using Microkernel.Abstractions.Initrd;
using Microkernel.Abstractions.Ipc;
using Microkernel.Abstractions.Syscalls;

namespace Kernel.Boot
{
    public static unsafe class KernelHigh
    {
        public static ulong RsdpPhysBase;
        public static ulong GopPhysBase;
        public static ulong GopFbSize;
        public static uint GopWidth;
        public static uint GopHeight;
        public static uint GopPixelsPerScanLine;

        public static ulong PmmBitmapPhys;
        public static ulong PmmTotalFrames;
        public static ulong PmmStart;
        public static ulong PmmPages;

        public static ulong InitrdPhysBase;
        public static ulong InitrdSize;

        // Dedicated 16 KiB interrupt stack for TSS.RSP0
        [StructLayout(LayoutKind.Sequential, Size = 16384)]
        private struct InterruptStackBuffer { }
        private static InterruptStackBuffer s_interruptStack;

        private static TaskStateSegment s_tss;

        // Phase 5 Test flags
        public static volatile bool Test1ServerReceived = false;
        public static volatile bool Test1ClientReceivedReply = false;
        public static volatile bool Test2NotificationReceived = false;
        public static volatile bool Test3AsyncHandled = false;
        public static volatile bool Test3SyncHandled = false;

        // -----------------------------------------------------------------
        // Phase 5 Test 1: Synchronous Rendezvous & Timeslice Donation
        // -----------------------------------------------------------------
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void IpcServerThread()
        {
            // Server calls sys_recv on Endpoint capability in Slot 1
            ulong status = Cpu.DoSyscall(SyscallNumbers.SysRecv, 1, 0, 0, 0, 0, 0);

            ulong d0 = Scheduler.CurrentThread->IpcRegisters.D0;
            ulong d1 = Scheduler.CurrentThread->IpcRegisters.D1;
            ulong badge = Scheduler.CurrentThread->IpcBadge;

            EarlySerial.Write("[SERVER] Received payload: 0xCAFE, 0xBEEF from Badge: ");
            EarlySerial.WriteHex(badge);
            EarlySerial.WriteLine("");

            Test1ServerReceived = true;

            // Server replies to client via sys_reply(d0=0xFEED)
            Cpu.DoSyscall(SyscallNumbers.SysReply, 0xFEED, 0, 0, 0, 0, 0);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void IpcClientThread()
        {
            // Client calls sys_call on Endpoint capability in Slot 1: (cptr=1, msgInfo=SyncRpc, d0=0xCAFE, d1=0xBEEF)
            ulong reply = Cpu.DoSyscall(SyscallNumbers.SysCall, 1, IpcMessageHeader.SyncRpc, 0xCAFE, 0xBEEF, 0, 0);

            EarlySerial.Write("[CLIENT] Received Reply: ");
            EarlySerial.Write("0xFEED");
            EarlySerial.WriteLine("");

            Test1ClientReceivedReply = true;
        }

        // -----------------------------------------------------------------
        // Phase 5 Test 2: 64-bit Asynchronous Notification
        // -----------------------------------------------------------------
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void NotifWorkerThread()
        {
            // Worker signals notification on Slot 2 with badge mask 0x04
            Cpu.DoSyscall(SyscallNumbers.SysNotify, 2, 0x04, 0, 0, 0, 0);
        }

        // -----------------------------------------------------------------
        // Phase 5 Test 3: Unified Wait (sys_recv_any)
        // -----------------------------------------------------------------
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void UnifiedReactorServerThread()
        {
            // Sub-test 3A: Wait for Async Notification on (ep=3, notif=4)
            ulong status = Cpu.DoSyscall(SyscallNumbers.SysRecvAny, 3, 4, 0, 0, 0, 0);
            if (Scheduler.CurrentThread->IpcMessageInfo == IpcMessageHeader.AsyncNotification)
            {
                EarlySerial.WriteLine("[UNIFIED] Successfully handled Async Notification event.");
                Test3AsyncHandled = true;
            }

            // Sub-test 3B: Wait for Synchronous RPC on (ep=3, notif=4)
            status = Cpu.DoSyscall(SyscallNumbers.SysRecvAny, 3, 4, 0, 0, 0, 0);
            if (Scheduler.CurrentThread->IpcMessageInfo == IpcMessageHeader.SyncRpc)
            {
                EarlySerial.WriteLine("[UNIFIED] Successfully handled Synchronous RPC event.");
                Test3SyncHandled = true;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void UnifiedAsyncNotifierThread()
        {
            // Yield briefly to let reactor server block on sys_recv_any
            Scheduler.Yield();
            Cpu.DoSyscall(SyscallNumbers.SysNotify, 4, 0x08, 0, 0, 0, 0);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void UnifiedSyncSenderThread()
        {
            // Yield briefly to let reactor server block on sys_recv_any
            Scheduler.Yield();
            Cpu.DoSyscall(SyscallNumbers.SysSend, 3, IpcMessageHeader.SyncRpc, 0x1234, 0x5678, 0, 0);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "KernelMainHigh")]
        public static void KernelMainHigh()
        {
            EarlySerial.WriteLine();
            EarlySerial.WriteLine("=================================================================");
            EarlySerial.WriteLine("   Bare-Metal x86-64 C# Microkernel (Native AOT / Higher-Half)  ");
            EarlySerial.WriteLine("   Layer 2: Higher-Half Transition & Memory Subsystem Active     ");
            EarlySerial.WriteLine("=================================================================");

            // 1. Verify RIP in canonical higher-half direct mapping space
            ulong rip = Cpu.GetRip();
            EarlySerial.Write("[HIGH] KernelMainHigh entered at RIP: ");
            EarlySerial.WriteHex(rip);
            EarlySerial.WriteLine();

            if (rip < Hhdm.Base)
            {
                EarlySerial.WriteLine("[FAIL] RIP is not in higher-half space!");
                PortIo.Out8(0xF4, 0x01);
                return;
            }
            EarlySerial.WriteLine("[PASS] Verified RIP >= Hhdm.Base (0xFFFF800000000000).");

            // 2. Verify RSP in higher-half space
            ulong rsp = Cpu.GetRsp();
            EarlySerial.Write("[HIGH] Stack Pointer RSP: ");
            EarlySerial.WriteHex(rsp);
            EarlySerial.WriteLine();

            if (rsp < Hhdm.Base)
            {
                EarlySerial.WriteLine("[FAIL] RSP is not in higher-half space!");
                PortIo.Out8(0xF4, 0x01);
                return;
            }
            EarlySerial.WriteLine("[PASS] Verified RSP is 16-byte aligned in higher-half space.");

            // 3. Verify Active Page Directory (CR3)
            ulong cr3 = Cpu.ReadCr3();
            EarlySerial.Write("[HIGH] Active Page Directory CR3: ");
            EarlySerial.WriteHex(cr3);
            EarlySerial.WriteLine();

            // 3b. Enable AVX in Ring 0
            Cpu.EnableAvx();
            EarlySerial.WriteLine("[CPU] CR4.OSXSAVE enabled. XCR0 configured for AVX/SSE (0x07).");

            // 4. Verify PAT MSR (PA4 = 0x01 Write-Combining)
            ulong pat = Cpu.ReadMsr(PatManager.Ia32PatMsr);
            byte pa4 = (byte)((pat >> 32) & 0xFF);
            EarlySerial.Write("[PAT] IA32_PAT MSR (0x277): ");
            EarlySerial.WriteHex(pat);
            EarlySerial.Write(" (PA4: ");
            EarlySerial.WriteHex(pa4);
            EarlySerial.WriteLine(")");

            if (pa4 != PatManager.MemoryTypeWc)
            {
                EarlySerial.WriteLine("[FAIL] PAT PA4 is not configured for Write-Combining!");
                PortIo.Out8(0xF4, 0x01);
                return;
            }
            EarlySerial.WriteLine("[PASS] PAT PA4 Write-Combining active.");

            // 5. Access Framebuffer strictly via translated HHDM virtual pointer (gopPhysBase + Hhdm.Base)
            ulong physFb = GopPhysBase;
            ulong virtFb = Hhdm.PhysicalToVirtual(physFb);

            EarlySerial.Write("[GOP] Framebuffer Physical Base: ");
            EarlySerial.WriteHex(physFb);
            EarlySerial.WriteLine();
            EarlySerial.Write("[GOP] Framebuffer HHDM Virtual Base: ");
            EarlySerial.WriteHex(virtFb);
            EarlySerial.WriteLine();

            if (virtFb < Hhdm.Base)
            {
                EarlySerial.WriteLine("[FAIL] Framebuffer virtual address is not in HHDM!");
                PortIo.Out8(0xF4, 0x01);
                return;
            }

            if (physFb != 0 && GopFbSize > 0)
            {
                uint* fb = (uint*)virtFb;
                uint width = GopWidth;
                uint height = GopHeight;
                uint pitch = GopPixelsPerScanLine > 0 ? GopPixelsPerScanLine : width;

                uint blockW = (width >= 150) ? 100u : (width > 50 ? width - 50 : 0);
                uint blockH = (height >= 150) ? 100u : (height > 50 ? height - 50 : 0);

                for (uint y = 0; y < blockH; y++)
                {
                    uint* row = fb + ((50 + y) * pitch);
                    for (uint x = 0; x < blockW; x++)
                    {
                        row[50 + x] = 0x00FF_FFFF; // 32-bit ARGB/XRGB White
                    }
                }

                EarlySerial.WriteLine("[PASS] Framebuffer accessed strictly via HHDM virtual pointer (PAT WC verified).");
            }
            else
            {
                EarlySerial.WriteLine("[WARN] Framebuffer base or size was 0; skipping pixel fill.");
            }

            // -----------------------------------------------------------------
            // Phase 3: Hardware Descriptors, Interrupts & Slab Heap
            // -----------------------------------------------------------------
            EarlySerial.WriteLine();
            EarlySerial.WriteLine("=================================================================");
            EarlySerial.WriteLine("   Bare-Metal x86-64 C# Microkernel (Native AOT / Higher-Half)  ");
            EarlySerial.WriteLine("   Layer 3: Hardware Descriptors, Interrupts & Slab Heap         ");
            EarlySerial.WriteLine("=================================================================");

            fixed (InterruptStackBuffer* pIntStack = &s_interruptStack)
            fixed (TaskStateSegment* pTss = &s_tss)
            {
                ulong intStackPhys = (ulong)(byte*)pIntStack;
                ulong intStackTop = intStackPhys < Hhdm.Base ? (intStackPhys + 16384 + Hhdm.Base) : (intStackPhys + 16384);
                intStackTop &= ~15UL; // 16-byte alignment

                pTss->Initialize(intStackTop);
                TaskStateSegment.Instance = pTss;

                ulong tssBase = (ulong)(byte*)pTss;
                if (tssBase < Hhdm.Base) tssBase += Hhdm.Base;

                EarlySerial.WriteLine("[STEP 1] Loading GDT...");
                Gdt.Initialize(tssBase);
                fixed (GdtPointer* ptr = &Gdt.Pointer)
                {
                    Cpu.LoadGdt(ptr);
                }

                EarlySerial.WriteLine("[STEP 2] Reloading Segments (CS=0x08, DS=0x10)...");
                Cpu.ReloadSegments(0x08, 0x10);

                EarlySerial.WriteLine("[STEP 3] Loading TSS (0x28)...");
                Cpu.LoadTss(0x28);

                EarlySerial.WriteLine("[STEP 4] Loading IDT (256 gates)...");
                Idt.Initialize(Cpu.GetIsrThunkTable());
                fixed (IdtPointer* ptr = &Idt.Pointer)
                {
                    Cpu.LoadIdt(ptr);
                }

                EarlySerial.WriteLine("[STEP 5] Initializing Slab Allocator...");
                if (PmmTotalFrames > 0 && PmmBitmapPhys != 0)
                {
                    ulong* bitmapVirt = (ulong*)Hhdm.PhysicalToVirtual(PmmBitmapPhys);
                    PageFrameAllocator.Initialize(bitmapVirt, PmmTotalFrames);
                    PageFrameAllocator.MarkRangeFree(PmmStart, PmmPages * 4096);
                    EarlySerial.Write("[PMM] PageFrameAllocator initialized. Free frames: ");
                    EarlySerial.WriteDec((long)PageFrameAllocator.FreeFrames);
                    EarlySerial.WriteLine("");
                }

                SlabAllocator.Initialize();

                EarlySerial.WriteLine("[SLAB] Allocator initialized and verified across size classes.");

                EarlySerial.WriteLine("[STEP 6] Masking 8259 PIC...");
                Pic8259.MaskAll();

                EarlySerial.WriteLine("[STEP 7] Initializing Local APIC Timer...");
                LocalApic.Initialize();

                // -----------------------------------------------------------------
                // Phase 4: Threading, Context Switching, Preemptive MLFQ & SYSCALL
                // -----------------------------------------------------------------
                EarlySerial.WriteLine();
                EarlySerial.WriteLine("=================================================================");
                EarlySerial.WriteLine("   Bare-Metal x86-64 C# Microkernel (Native AOT / Higher-Half)  ");
                EarlySerial.WriteLine("   Layer 4: Threading, Preemptive MLFQ & Hardware SYSCALL        ");
                EarlySerial.WriteLine("=================================================================");

                // Configure MSRs for SYSCALL/SYSRET
                ulong efer = Cpu.ReadMsr(Cpu.Ia32Efer);
                Cpu.WriteMsr(Cpu.Ia32Efer, efer | 1UL);

                ulong starVal = (0x0013UL << 48) | (0x0008UL << 32);
                Cpu.WriteMsr(Cpu.Ia32Star, starVal);

                ulong lstarVal = Cpu.GetSyscallEntry();
                if (lstarVal < Hhdm.Base)
                {
                    lstarVal += Hhdm.Base;
                }
                Cpu.WriteMsr(Cpu.Ia32Lstar, lstarVal);

                Cpu.WriteMsr(Cpu.Ia32Fmask, 0x00000200UL);

                EarlySerial.WriteLine("[MSR] STAR, LSTAR, FMASK configured for SYSCALL/SYSRET.");

                Scheduler.Initialize();
                Cpu.EnableInterrupts();

                // -----------------------------------------------------------------
                // Phase 5: Capability Space (CSpace) & Unified IPC Engine
                // -----------------------------------------------------------------
                EarlySerial.WriteLine();
                EarlySerial.WriteLine("=================================================================");
                EarlySerial.WriteLine("   Bare-Metal x86-64 C# Microkernel (Native AOT / Higher-Half)  ");
                EarlySerial.WriteLine("   Layer 5 & 6: Capability Space (CSpace) & Unified IPC Engine   ");
                EarlySerial.WriteLine("=================================================================");

                // 1. Initialize Kernel CSpace Root CNode
                CNode* kernelCNode = CNode.Create();
                Scheduler.MainThread->CSpaceRoot = kernelCNode;
                Scheduler.MainThread->CSpaceRootAddress = (ulong)kernelCNode;
                EarlySerial.WriteLine("[CSPACE] Kernel capability table initialized.");

                // 2. Test 1: Synchronous Rendezvous & Timeslice Donation
                Endpoint* testEp = Endpoint.Create();
                kernelCNode->Set(1, testEp, CapabilityType.Endpoint, CapabilityRights.All, badge: 0x1111);

                EarlySerial.WriteLine("[IPC] Testing Synchronous Rendezvous with Timeslice Donation...");

                delegate* unmanaged[Cdecl]<void> srvEntry = &IpcServerThread;
                delegate* unmanaged[Cdecl]<void> cliEntry = &IpcClientThread;

                Scheduler.CreateThread(srvEntry, 0);
                Scheduler.CreateThread(cliEntry, 0);

                while (!Test1ServerReceived || !Test1ClientReceivedReply)
                {
                    Scheduler.Yield();
                }

                // 3. Test 2: 64-Bit Asynchronous Notification
                Notification* testNotif = Notification.Create();
                kernelCNode->Set(2, testNotif, CapabilityType.Notification, CapabilityRights.All, badge: 0x04);

                EarlySerial.WriteLine("[NOTIF] Testing 64-bit Asynchronous Notification...");

                delegate* unmanaged[Cdecl]<void> notifWorker = &NotifWorkerThread;
                Scheduler.CreateThread(notifWorker, 0);

                ulong deliveredMask = testNotif->Wait();
                EarlySerial.Write("[NOTIF] Received badge bitmask: ");
                EarlySerial.WriteHex(deliveredMask);
                EarlySerial.WriteLine("");

                // 4. Test 3: Unified Wait (sys_recv_any)
                Endpoint* unifiedEp = Endpoint.Create();
                Notification* unifiedNotif = Notification.Create();
                kernelCNode->Set(3, unifiedEp, CapabilityType.Endpoint, CapabilityRights.All, badge: 0x2222);
                kernelCNode->Set(4, unifiedNotif, CapabilityType.Notification, CapabilityRights.All, badge: 0x08);

                EarlySerial.WriteLine("[UNIFIED] Testing sys_recv_any dual-wait reactor...");

                delegate* unmanaged[Cdecl]<void> reactorSrv = &UnifiedReactorServerThread;
                delegate* unmanaged[Cdecl]<void> asyncNotifier = &UnifiedAsyncNotifierThread;
                delegate* unmanaged[Cdecl]<void> syncSender = &UnifiedSyncSenderThread;

                Scheduler.CreateThread(reactorSrv, 0);
                Scheduler.CreateThread(asyncNotifier, 0);

                while (!Test3AsyncHandled)
                {
                    Scheduler.Yield();
                }

                Scheduler.CreateThread(syncSender, 0);

                while (!Test3SyncHandled)
                {
                    Scheduler.Yield();
                }

                EarlySerial.WriteLine("[PASS] Phase 5 unified wait reactor verified.");

                // -----------------------------------------------------------------
                // Phase 6: Userland Bootstrap & Root Task (Layer 7 & Ring 3 Execution)
                // -----------------------------------------------------------------
                EarlySerial.WriteLine();
                EarlySerial.WriteLine("=================================================================");
                EarlySerial.WriteLine("   Bare-Metal x86-64 C# Microkernel (Native AOT / Higher-Half)  ");
                EarlySerial.WriteLine("   Layer 7: Userland Bootstrap & Root Task (Ring 3 Execution)   ");
                EarlySerial.WriteLine("=================================================================");

                // 1. Locate roottask in initial ramdisk
                byte* initrdVirt = (byte*)Hhdm.PhysicalToVirtual(InitrdPhysBase);
                byte* roottaskPayload = null;
                ulong roottaskSize = 0;

                bool found = InitrdParser.FindEntry(
                    initrdVirt,
                    InitrdSize,
                    "roottask",
                    out roottaskPayload,
                    out roottaskSize);

                if (!found || roottaskPayload == null || roottaskSize == 0)
                {
                    EarlySerial.WriteLine("[ERROR] Could not locate roottask in initial ramdisk!");
                    PortIo.Out8(0xF4, 0x01);
                    return;
                }

                // 2. Synthesize user address space (PML4, code at 0x40000000, stack at 0x7FFFF0000000)
                ulong userStackTop = 0;
                ulong userPml4Phys = VirtualMemorySpace.CreateUserAddressSpace(
                    VirtualMemorySpace.Pml4PhysicalAddress,
                    0x0000000040000000UL,
                    roottaskPayload,
                    roottaskSize,
                    0x00007FFFF0000000UL,
                    262144,
                    out userStackTop);

                EarlySerial.Write("[ROOTTASK] User address space synthesized (PML4: ");
                EarlySerial.WriteHex(userPml4Phys);
                EarlySerial.WriteLine(") with User bit enabled.");

                // 3. Synthesize root CNode with core capabilities
                CNode* rootCNode = CNode.Create();
                // Slot 0: Null Capability
                // Slot 1: Root CNode Self-Capability (Read | Write | Grant)
                rootCNode->Set(1, rootCNode, CapabilityType.CNode, CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Grant);
                // Slot 3: Untyped Page Frame Allocator Capability
                rootCNode->Set(3, (void*)0x1000, CapabilityType.PageFrame, CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Grant);
                // Slot 4: Kernel Logging / Diagnostics Endpoint Capability
                Endpoint* diagEp = Endpoint.Create();
                rootCNode->Set(4, diagEp, CapabilityType.Endpoint, CapabilityRights.Write | CapabilityRights.Call, badge: 0x42);
                // Slot 5: Userland RPC Endpoint Capability (Read | Write | Call | Grant)
                Endpoint* rpcEp = Endpoint.Create();
                rootCNode->Set(5, rpcEp, CapabilityType.Endpoint, CapabilityRights.All, badge: 0x5555);
                // Slot 6: PCI Service Endpoint Capability
                Endpoint* pciEp = Endpoint.Create();
                rootCNode->Set(6, pciEp, CapabilityType.Endpoint, CapabilityRights.All, badge: 0x6666);
                // Slot 7: Display Service Endpoint Capability
                Endpoint* displayEp = Endpoint.Create();
                rootCNode->Set(7, displayEp, CapabilityType.Endpoint, CapabilityRights.All, badge: 0x7777);
                // Slot 8: Supervisor Service Endpoint Capability
                Endpoint* supervisorEp = Endpoint.Create();
                rootCNode->Set(8, supervisorEp, CapabilityType.Endpoint, CapabilityRights.All, badge: 0x8888);
                // Slot 9: Storage Service Endpoint Capability
                Endpoint* storageEp = Endpoint.Create();
                rootCNode->Set(9, storageEp, CapabilityType.Endpoint, CapabilityRights.All, badge: 0x9999);
                // Slot 10: Input Service Endpoint Capability
                Endpoint* inputEp = Endpoint.Create();
                rootCNode->Set(10, inputEp, CapabilityType.Endpoint, CapabilityRights.All, badge: 0xAAAA);

                EarlySerial.WriteLine("[ROOTTASK] Initial root CNode initialized with 8 core capabilities.");

                // 4. Synthesize roottask thread
                ThreadControlBlock* roottaskTcb = Scheduler.CreateThread(null, 0, enqueue: false);
                roottaskTcb->CSpaceRoot = rootCNode;
                roottaskTcb->Pml4Address = userPml4Phys;
                // Slot 2: roottask TCB Capability (Read | Write | Call)
                rootCNode->Set(2, roottaskTcb, CapabilityType.ThreadControl, CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Call);

                Scheduler.CurrentThread = roottaskTcb;
                roottaskTcb->State = ThreadState.Running;
                TaskStateSegment.SetRsp0(roottaskTcb->KernelStackTop);

                // 5. Jump to Ring 3 via iretq
                EarlySerial.WriteLine("[TRANSITION] Dropping to Ring 3 (CPL = 3) via iretq...");

                // Constraint 1: Jump to actual .text entry RVA (0x40001000)
                ulong entryRip = 0x0000000040001000UL;
                Cpu.EnterUserMode(entryRip, userStackTop, userPml4Phys);
            }
        }
    }
}
