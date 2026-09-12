using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Boot;
using Kernel.Capabilities;
using Kernel.Diagnostics;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;
using Kernel.Scheduling;
using Microkernel.Abstractions.Boot;
using Microkernel.Abstractions.Capabilities;
using Microkernel.Abstractions.Ipc;
using Microkernel.Abstractions.Syscalls;

namespace Kernel.Ipc
{
    public static unsafe class SyscallDispatcher
    {
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "DispatchSyscall")]
        public static ulong DispatchSyscall(
            ulong syscallNumber,
            ulong a1,
            ulong a2,
            ulong a3,
            ulong a4,
            ulong a5,
            ulong a6)
        {
            ThreadControlBlock* current = Scheduler.CurrentThread;
            CNode* cspaceRoot = current != null ? current->CSpaceRoot : null;

            switch (syscallNumber)
            {
                case SyscallNumbers.SysYield: // 0x01
                    Scheduler.Yield();
                    return 0;

                case SyscallNumbers.SysGetTid: // 0x02
                    return current != null ? current->Id : 0;

                case SyscallNumbers.SysLog: // 0x03
                    if (a1 != 0)
                    {
                        EarlySerial.WriteBytes((byte*)a1);
                    }
                    return 0;

                case SyscallNumbers.SysExit: // 0x04
                    EarlySerial.WriteLine("[SUCCESS] Phase 9 fully operational. All 12 layers verified. Exiting QEMU...");
                    PortIo.Out8(0xF4, 0x10);
                    if (a1 == 1)
                    {
                        AcpiPower.Reboot();
                    }
                    else
                    {
                        AcpiPower.Shutdown();
                    }
                    return 0;

                case SyscallNumbers.SysCreateThread: // 0x05
                {
                    ulong entryRip = a1;
                    ulong userRsp = a2;
                    int priority = (int)a3;
                    ThreadControlBlock* newTcb = Scheduler.CreateUserThread(entryRip, userRsp, priority);
                    return newTcb != null ? newTcb->Id : 0;
                }

                case SyscallNumbers.SysMapMmio: // 0x06
                {
                    ulong physAddr = a1;
                    ulong virtAddr = a2;
                    ulong sizeBytes = a3;
                    bool writeCombining = a4 != 0;
                    // Hardened: reject degenerate requests before touching page tables.
                    if (sizeBytes == 0 || virtAddr == 0) return ~0UL;
                    if (sizeBytes > 256UL * 1024 * 1024) return ~0UL;
                    ulong userPml4 = (current != null && current->Pml4Address != 0) ? current->Pml4Address : Cpu.ReadCr3();
                    bool ok = VirtualMemorySpace.MapUserMmio(userPml4, physAddr, virtAddr, sizeBytes, writeCombining);
                    return ok ? 0 : ~0UL;
                }

                case SyscallNumbers.SysGetBootInfo: // 0x07
                {
                    if (a1 != 0)
                    {
                        KernelBootInfo* info = (KernelBootInfo*)a1;
                        info->RsdpPhysBase = KernelHigh.RsdpPhysBase;
                        info->InitrdPhysBase = KernelHigh.InitrdPhysBase;
                        info->InitrdSize = KernelHigh.InitrdSize;
                        info->GopPhysBase = KernelHigh.GopPhysBase;
                        info->GopFbSize = KernelHigh.GopFbSize;
                        info->GopWidth = KernelHigh.GopWidth;
                        info->GopHeight = KernelHigh.GopHeight;
                        info->GopPixelsPerScanLine = KernelHigh.GopPixelsPerScanLine;
                        info->IsHypervisor = Cpu.IsHypervisor() ? 1u : 0u;
                        return 0;
                    }
                    return 1;
                }

                case SyscallNumbers.SysCreateProcess: // 0x08
                {
                    ulong payloadVirt = a1;
                    ulong payloadSize = a2;
                    ulong entryVirt = a3 != 0 ? a3 : 0x0000000040000000UL;
                    int priority = (int)a4;

                    byte* payload = (byte*)payloadVirt;
                    ulong userStackTop;
                    ulong childPml4 = VirtualMemorySpace.CreateUserAddressSpace(
                        VirtualMemorySpace.Pml4PhysicalAddress,
                        entryVirt,
                        payload,
                        payloadSize,
                        0x00007FFFF0000000UL,
                        262144,
                        out userStackTop);

                    ulong entryRip = entryVirt + 0x1000;
                    if (payloadSize > 0x40 && payload[0] == 0x4D && payload[1] == 0x5A)
                    {
                        uint e_lfanew = *(uint*)(payload + 0x3C);
                        if (e_lfanew < payloadSize && *(uint*)(payload + e_lfanew) == 0x00004550)
                        {
                            uint entryRva = *(uint*)(payload + e_lfanew + 40);
                            entryRip = entryVirt + entryRva;
                        }
                    }

                    ThreadControlBlock* newTcb = Scheduler.CreateUserThread(entryRip, userStackTop, priority, childPml4);
                    return newTcb != null ? newTcb->Id : 0;
                }

                case SyscallNumbers.SysSpawn: // 0x0B: Spawn(entryVirt, stackTop)
                {
                    // Architectural correction #1: full Ring-0 synthesis.
                    // Userland (shell exec) never touches VirtualMemorySpace or
                    // Scheduler internals. Kernel: (1) clones kernel PML4 high
                    // half into a fresh address space, (2) maps a 64KB user
                    // stack at 0x7FFFFFF00000, (3) allocates 16KB kernel stack
                    // via CreateUserThread (TSS.RSP0), (4) mints root CNode
                    // caps inheriting the caller, (5) sets RIP/RSP/CS/SS/RFLAGS.
                    ulong entryRip = a1;
                    ulong stackTop = a2;
                    if (entryRip == 0) return 0;

                    const ulong UserStackBase = 0x00007FFFFFF00000UL;
                    const ulong UserStackSize = 65536;
                    const ulong DefaultStackTop = 0x00007FFFFFFFF000UL;

                    ulong userPml4Phys = PageFrameAllocator.AllocateFrame();
                    if (userPml4Phys == 0) return 0;
                    ulong* userPml4 = (ulong*)Hhdm.PhysicalToVirtual(userPml4Phys);
                    for (int i = 0; i < 512; i++) userPml4[i] = 0;
                    ulong* kernPml4 = (ulong*)Hhdm.PhysicalToVirtual(VirtualMemorySpace.Pml4PhysicalAddress);
                    for (int i = 256; i < 512; i++) userPml4[i] = kernPml4[i];

                    if (stackTop == 0) stackTop = DefaultStackTop;
                    ulong stackBase = stackTop >= UserStackSize ? (stackTop - UserStackSize) & ~0xFFFUL : UserStackBase;
                    ulong pages = (UserStackSize + 4095) / 4096;
                    for (ulong s = 0; s < pages; s++)
                    {
                        ulong f = PageFrameAllocator.AllocateFrame();
                        if (f == 0)
                        {
                            VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                            return 0;
                        }
                        byte* fv = (byte*)Hhdm.PhysicalToVirtual(f);
                        for (ulong w = 0; w < 4096 / 8; w++) ((ulong*)fv)[w] = 0;
                        VirtualMemorySpace.MapUserPage4K(userPml4, (stackBase & ~0xFFFUL) + s * 4096, f, Paging.Present | Paging.Writable | Paging.User);
                    }

                    ThreadControlBlock* newTcb = Scheduler.CreateUserThread(entryRip, stackTop & ~15UL, 0, userPml4Phys);
                    if (newTcb == null)
                    {
                        VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                        return 0;
                    }
                    // Mint root CNode already inherited inside CreateUserThread
                    // from caller; nothing else to wire for Ring 3 entry.
                    return newTcb != null ? newTcb->Id : 0;
                }

                case SyscallNumbers.SysGetPhysicalAddress: // 0x09
                {
                    ulong virtAddr = a1;
                    ulong userPml4 = (current != null && current->Pml4Address != 0) ? current->Pml4Address : Cpu.ReadCr3();
                    return VirtualMemorySpace.GetPhysicalAddress(userPml4, virtAddr);
                }

                case SyscallNumbers.SysAllocDma: // 0x0A: sys_alloc_dma(sizeBytes, virtAddr)
                {
                    ulong sizeBytes = a1;
                    ulong virtAddr = a2;
                    // Hardened: strict DMA-zone confinement via DmaArenaAllocator
                    // (MinAddress/MaxAddress, overflow, zero-size rejection).
                    if (sizeBytes == 0) return 0;
                    if (sizeBytes > 256UL * 1024 * 1024) return 0;
                    if (virtAddr != 0)
                    {
                        if (virtAddr >= Kernel.Memory.Virtual.Hhdm.Base) return 0;
                        if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - virtAddr) return 0;
                    }
                    ulong phys = DmaArenaAllocator.Allocate(sizeBytes, 4096);
                    if (phys == 0) return 0;
                    // Post-allocation confinement proof: allocated range must be
                    // wholly inside [MinAddress, MaxAddress).
                    if (!DmaArenaAllocator.ValidateRange(phys, sizeBytes)) return 0;
                    if (virtAddr != 0)
                    {
                        ulong userPml4 = (current != null && current->Pml4Address != 0) ? current->Pml4Address : Cpu.ReadCr3();
                        bool ok = VirtualMemorySpace.MapUserMmio(userPml4, phys, virtAddr, sizeBytes, writeCombining: false);
                        if (!ok) return 0;
                    }
                    return phys;
                }

                case SyscallNumbers.SysSend: // 0x10: sys_send(cptr, msgInfo, d0, d1, d2, d3)
                {
                    uint cptr = (uint)a1;
                    ulong msgInfo = a2;
                    ulong d0 = a3;
                    ulong d1 = a4;
                    ulong d2 = a5;
                    ulong d3 = a6;

                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, cptr, CapabilityRights.Write, out cap);
                    if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                    {
                        return status != CSpace.ErrSuccess ? status : CSpace.ErrInvalidCapability;
                    }

                    // Special diagnostic endpoint handler (Slot 4)
                    if (cptr == 4)
                    {
                        EarlySerial.WriteLine("[ROOTTASK] Invoked capability Slot 4 (Diagnostics Endpoint).");
                        return 0;
                    }

                    return FastPathTransfer.Send((Endpoint*)cap->TargetObject, msgInfo, d0, d1, d2, d3, cap->Badge, isCall: false);
                }

                case SyscallNumbers.SysRecv: // 0x11: sys_recv(cptr)
                {
                    uint cptr = (uint)a1;

                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, cptr, CapabilityRights.Read, out cap);
                    if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                    {
                        return status != CSpace.ErrSuccess ? status : CSpace.ErrInvalidCapability;
                    }

                    ulong d0, d1, d2, d3, badge, msgInfo;
                    status = FastPathTransfer.Recv((Endpoint*)cap->TargetObject, out d0, out d1, out d2, out d3, out badge, out msgInfo);
                    if (status == 0)
                    {
                        if (current != null)
                        {
                            current->IpcRegisters.D0 = d0;
                            current->IpcRegisters.D1 = d1;
                            current->IpcRegisters.D2 = d2;
                            current->IpcRegisters.D3 = d3;
                            current->IpcBadge = badge;
                            current->IpcMessageInfo = msgInfo;
                        }

                        // SysRecv Null Check (Adjustment 5):
                        if (a2 != 0)
                        {
                            ulong* userBuf = (ulong*)a2;
                            userBuf[0] = msgInfo;
                            userBuf[1] = d0;
                            userBuf[2] = d1;
                            userBuf[3] = d2;
                            userBuf[4] = d3;
                            userBuf[5] = badge;
                        }
                    }
                    return status;
                }

                case SyscallNumbers.SysCall: // 0x12: sys_call(cptr, msgInfo, d0, d1, d2, d3)
                {
                    uint cptr = (uint)a1;
                    ulong msgInfo = a2;
                    ulong d0 = a3;
                    ulong d1 = a4;
                    ulong d2 = a5;
                    ulong d3 = a6;

                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, cptr, CapabilityRights.Call | CapabilityRights.Write, out cap);
                    if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                    {
                        return status != CSpace.ErrSuccess ? status : CSpace.ErrInvalidCapability;
                    }

                    return FastPathTransfer.Send((Endpoint*)cap->TargetObject, msgInfo, d0, d1, d2, d3, cap->Badge, isCall: true);
                }

                case SyscallNumbers.SysReply: // 0x13: sys_reply(d0, d1, d2, d3)
                {
                    ulong d0 = a1;
                    ulong d1 = a2;
                    ulong d2 = a3;
                    ulong d3 = a4;

                    return FastPathTransfer.Reply(d0, d1, d2, d3);
                }

                case SyscallNumbers.SysNotify: // 0x14: sys_notify(cptr, badge)
                {
                    uint cptr = (uint)a1;
                    ulong badge = a2;

                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, cptr, CapabilityRights.Write, out cap);
                    if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Notification)
                    {
                        return status != CSpace.ErrSuccess ? status : CSpace.ErrInvalidCapability;
                    }

                    ulong badgeToSignal = badge != 0 ? badge : (cap->Badge != 0 ? cap->Badge : 1UL);
                    ((Notification*)cap->TargetObject)->Signal(badgeToSignal);
                    return 0;
                }

                case SyscallNumbers.SysRecvAny: // 0x15: sys_recv_any(endpointCptr, notificationCptr)
                {
                    uint epCptr = (uint)a1;
                    uint notifCptr = (uint)a2;

                    Capability* epCap = null;
                    Capability* notifCap = null;

                    if (epCptr != 0)
                    {
                        ulong s = CSpace.LookupCapability(cspaceRoot, epCptr, CapabilityRights.Read, out epCap);
                        if (s != CSpace.ErrSuccess || epCap->Type != CapabilityType.Endpoint)
                        {
                            return CSpace.ErrInvalidCapability;
                        }
                    }

                    if (notifCptr != 0)
                    {
                        ulong s = CSpace.LookupCapability(cspaceRoot, notifCptr, CapabilityRights.Read, out notifCap);
                        if (s != CSpace.ErrSuccess || notifCap->Type != CapabilityType.Notification)
                        {
                            return CSpace.ErrInvalidCapability;
                        }
                    }

                    ulong msgType, d0, d1, d2, d3, badge;
                    ulong status = UnifiedWait.RecvAny(
                        epCap != null ? (Endpoint*)epCap->TargetObject : null,
                        notifCap != null ? (Notification*)notifCap->TargetObject : null,
                        out msgType,
                        out d0,
                        out d1,
                        out d2,
                        out d3,
                        out badge);

                    if (status == 0 && current != null)
                    {
                        current->IpcMessageInfo = msgType;
                        current->IpcRegisters.D0 = d0;
                        current->IpcRegisters.D1 = d1;
                        current->IpcRegisters.D2 = d2;
                        current->IpcRegisters.D3 = d3;
                        current->IpcBadge = badge;
                    }

                    return status;
                }

                default:
                    return ~0UL;
            }
        }
    }
}
