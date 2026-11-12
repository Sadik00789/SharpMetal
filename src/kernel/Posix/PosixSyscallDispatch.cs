using System;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Capabilities;
using Kernel.Diagnostics;
using Kernel.Ipc;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;
using Kernel.Scheduling;
using Microkernel.Abstractions.Capabilities;
using Microkernel.Abstractions.Syscalls;

namespace Kernel.Posix
{
    public static unsafe class PosixSyscallDispatch
    {
        // Standard Linux x86-64 syscall numbers
        public const ulong SYS_read        = 0;
        public const ulong SYS_write       = 1;
        public const ulong SYS_open        = 2;
        public const ulong SYS_close       = 3;
        public const ulong SYS_mmap        = 9;
        public const ulong SYS_brk         = 12;
        public const ulong SYS_socket      = 41;
        public const ulong SYS_exit        = 60;
        public const ulong SYS_exit_group  = 231;

        // Linux Errno definitions
        public const long EPERM   = 1;
        public const long ENOENT  = 2;
        public const long ESRCH   = 3;
        public const long EIO     = 5;
        public const long EBADF   = 9;
        public const long EAGAIN  = 11;
        public const long ENOMEM  = 12;
        public const long EACCES  = 13;
        public const long EFAULT  = 14;
        public const long EINVAL  = 22;
        public const long EMFILE  = 24;
        public const long ENOSYS  = 38;

        private static bool IsUserRange(void* ptr, ulong len)
        {
            ulong addr = (ulong)ptr;
            if (addr == 0 || addr >= Hhdm.Base) return false;
            return len <= Hhdm.Base - addr;
        }

        private static ulong AllocBounceFrame(out byte* virt)
        {
            ulong phys = PageFrameAllocator.AllocateFrame();
            if (phys == 0)
            {
                virt = null;
                return 0;
            }
            virt = (byte*)Hhdm.PhysicalToVirtual(phys);
            ulong* p = (ulong*)virt;
            for (int i = 0; i < 512; i++) p[i] = 0;
            return phys;
        }

        private static void FreeBounceFrame(ulong phys)
        {
            if (phys != 0)
            {
                PageFrameAllocator.FreeFrame(phys);
            }
        }

        public static ulong Dispatch(
            ulong syscallNumber,
            ulong a1,
            ulong a2,
            ulong a3,
            ulong a4,
            ulong a5,
            ulong a6)
        {
            ThreadControlBlock* current = Scheduler.CurrentThread;
            ulong pml4Phys = (current != null && current->Pml4Address != 0) ? current->Pml4Address : Cpu.ReadCr3();
            ProcessControlBlock* pcb = VirtualMemorySpace.GetOrCreateProcess(pml4Phys);
            CNode* cspaceRoot = current != null ? current->CSpaceRoot : null;

            switch (syscallNumber)
            {
                case SYS_read:
                    return SysRead(pcb, cspaceRoot, (int)a1, (byte*)a2, a3);

                case SYS_write:
                    return SysWrite(pcb, cspaceRoot, (int)a1, (byte*)a2, a3);

                case SYS_open:
                    return SysOpen(pcb, cspaceRoot, (byte*)a1, (uint)a2, (uint)a3);

                case SYS_close:
                    return SysClose(pcb, cspaceRoot, (int)a1);

                case SYS_mmap:
                    return SysMmap(pml4Phys, a1, a2, (uint)a3, (uint)a4, (int)a5, a6);

                case SYS_brk:
                    return SysBrk(pml4Phys, a1);

                case SYS_socket:
                    return SysSocket(pcb, cspaceRoot, (int)a1, (int)a2, (int)a3);

                case SYS_exit:
                case SYS_exit_group:
                    return SysExit(current, cspaceRoot, (int)a1);

                case SyscallNumbers.SysSetAbi: // 0x26: allow explicit ABI switch back if requested
                    if (current != null) current->AbiMode = (int)a1;
                    return 0;

                default:
                    // Invariant: NEVER panic on unknown syscall number
                    return unchecked((ulong)-ENOSYS);
            }
        }

        private static ulong SysRead(ProcessControlBlock* pcb, CNode* cspaceRoot, int fd, byte* buf, ulong count)
        {
            if (count == 0) return 0;

            PosixFdEntry entry;
            if (!PosixFdTable.GetFd(pcb, fd, out entry))
            {
                return unchecked((ulong)-EBADF);
            }

            // fd 0 / Stdin: Invariant 2 Blocking I/O Yield
            if (entry.Type == PosixFdType.Stdin)
            {
                if (!IsUserRange(buf, 1)) return unchecked((ulong)-EFAULT);
                if (cspaceRoot == null) return unchecked((ulong)-EIO);

                Capability* cap;
                ulong status = CSpace.LookupCapability(cspaceRoot, 10, CapabilityRights.Call | CapabilityRights.Write, out cap);
                if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                {
                    return unchecked((ulong)-EIO);
                }

                // Invariant 2: Enable interrupts before blocking IPC
                Cpu.EnableInterrupts();

                // Loop ReadKey (method 1) against InputService endpoint 10; yield when 0
                for (int iter = 0; iter < 100000; iter++)
                {
                    ulong key = FastPathTransfer.Send(
                        (Endpoint*)cap->TargetObject,
                        msgInfo: 1, // ReadKey method 1
                        d0: 0,
                        d1: 0,
                        d2: 0,
                        d3: 0,
                        badge: cap->Badge,
                        isCall: true);

                    if (key != 0 && key != unchecked((ulong)-1))
                    {
                        buf[0] = (byte)key;
                        return 1;
                    }

                    Scheduler.Yield();
                }

                // Bounded timeout elapsed without input
                return unchecked((ulong)-EAGAIN);
            }

            // fd >= 3: VFS File Handle
            if (entry.Type == PosixFdType.Vfs)
            {
                if (cspaceRoot == null) return unchecked((ulong)-EIO);

                Capability* cap;
                ulong status = CSpace.LookupCapability(cspaceRoot, 11, CapabilityRights.Call | CapabilityRights.Write, out cap);
                if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                {
                    return unchecked((ulong)-EIO);
                }

                ulong chunk = count < 4096 ? count : 4096;
                if (!IsUserRange(buf, chunk)) return unchecked((ulong)-EFAULT);

                ulong vfsPhys = AllocBounceFrame(out byte* src);
                if (vfsPhys == 0) return unchecked((ulong)-ENOMEM);
                try
                {
                    Cpu.EnableInterrupts();
                    ulong readBytes = FastPathTransfer.Send(
                        (Endpoint*)cap->TargetObject,
                        msgInfo: 2, // Read method 2
                        d0: (ulong)entry.ServerHandle,
                        d1: vfsPhys,
                        d2: entry.CurrentOffset,
                        d3: chunk,
                        badge: cap->Badge,
                        isCall: true);

                    if (readBytes == 0 || readBytes > chunk)
                    {
                        return 0; // EOF or read failure
                    }

                    for (ulong b = 0; b < readBytes; b++)
                    {
                        buf[b] = src[b];
                    }

                    PosixFdTable.UpdateOffset(pcb, fd, entry.CurrentOffset + readBytes);
                    return readBytes;
                }
                finally
                {
                    FreeBounceFrame(vfsPhys);
                }
            }

            // fd >= 3: Socket Handle
            if (entry.Type == PosixFdType.Socket)
            {
                if (cspaceRoot == null) return unchecked((ulong)-EIO);

                Capability* cap;
                ulong status = CSpace.LookupCapability(cspaceRoot, 12, CapabilityRights.Call | CapabilityRights.Write, out cap);
                if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                {
                    return unchecked((ulong)-EIO);
                }

                ulong chunk = count < 4096 ? count : 4096;
                if (!IsUserRange(buf, chunk)) return unchecked((ulong)-EFAULT);

                ulong netPhys = AllocBounceFrame(out byte* src);
                if (netPhys == 0) return unchecked((ulong)-ENOMEM);
                try
                {
                    Cpu.EnableInterrupts();
                    ulong recvd = FastPathTransfer.Send(
                        (Endpoint*)cap->TargetObject,
                        msgInfo: 10, // Recv method 10
                        d0: (ulong)entry.ServerHandle,
                        d1: netPhys,
                        d2: chunk,
                        d3: 0,
                        badge: cap->Badge,
                        isCall: true);

                    if (recvd == 0 || recvd > chunk)
                    {
                        return 0;
                    }

                    for (ulong b = 0; b < recvd; b++)
                    {
                        buf[b] = src[b];
                    }

                    return recvd;
                }
                finally
                {
                    FreeBounceFrame(netPhys);
                }
            }

            return unchecked((ulong)-EBADF);
        }

        private static ulong SysWrite(ProcessControlBlock* pcb, CNode* cspaceRoot, int fd, byte* buf, ulong count)
        {
            if (count == 0) return 0;

            PosixFdEntry entry;
            if (!PosixFdTable.GetFd(pcb, fd, out entry))
            {
                return unchecked((ulong)-EBADF);
            }

            // fd 1 & 2 / Stdout & Stderr: EarlySerial serial console writes
            if (entry.Type == PosixFdType.Stdout || entry.Type == PosixFdType.Stderr)
            {
                if (!IsUserRange(buf, count)) return unchecked((ulong)-EFAULT);
                for (ulong i = 0; i < count; i++)
                {
                    EarlySerial.WriteChar((char)buf[i]);
                }
                return count;
            }

            // fd >= 3: Socket Send
            if (entry.Type == PosixFdType.Socket)
            {
                if (cspaceRoot == null) return unchecked((ulong)-EIO);

                Capability* cap;
                ulong status = CSpace.LookupCapability(cspaceRoot, 12, CapabilityRights.Call | CapabilityRights.Write, out cap);
                if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                {
                    return unchecked((ulong)-EIO);
                }

                ulong chunk = count < 4096 ? count : 4096;
                if (!IsUserRange(buf, chunk)) return unchecked((ulong)-EFAULT);

                ulong netPhys = AllocBounceFrame(out byte* dst);
                if (netPhys == 0) return unchecked((ulong)-ENOMEM);
                try
                {
                    for (ulong b = 0; b < chunk; b++)
                    {
                        dst[b] = buf[b];
                    }

                    Cpu.EnableInterrupts();
                    ulong sent = FastPathTransfer.Send(
                        (Endpoint*)cap->TargetObject,
                        msgInfo: 9, // Send method 9
                        d0: (ulong)entry.ServerHandle,
                        d1: netPhys,
                        d2: chunk,
                        d3: 0,
                        badge: cap->Badge,
                        isCall: true);

                    return sent;
                }
                finally
                {
                    FreeBounceFrame(netPhys);
                }
            }

            return unchecked((ulong)-EBADF);
        }

        private static ulong SysOpen(ProcessControlBlock* pcb, CNode* cspaceRoot, byte* pathVirt, uint flags, uint mode)
        {
            if (!IsUserRange(pathVirt, 1)) return unchecked((ulong)-EFAULT);
            if (cspaceRoot == null) return unchecked((ulong)-EIO);

            Capability* cap;
            ulong status = CSpace.LookupCapability(cspaceRoot, 11, CapabilityRights.Call | CapabilityRights.Write, out cap);
            if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
            {
                return unchecked((ulong)-EIO);
            }

            ulong maxScan = Hhdm.Base - (ulong)pathVirt;
            int scanCap = maxScan < 255 ? (int)maxScan : 255;

            ulong pathPhys = AllocBounceFrame(out byte* pathDst);
            if (pathPhys == 0) return unchecked((ulong)-ENOMEM);
            try
            {
                int len = 0;
                while (len < scanCap && pathVirt[len] != 0)
                {
                    pathDst[len] = pathVirt[len];
                    len++;
                }
                if (len == scanCap && scanCap < 255) return unchecked((ulong)-EFAULT);
                pathDst[len] = 0;

                if (len == 0) return unchecked((ulong)-ENOENT);

                Cpu.EnableInterrupts();
                ulong handle = FastPathTransfer.Send(
                    (Endpoint*)cap->TargetObject,
                    msgInfo: 1, // Open method 1
                    d0: pathPhys,
                    d1: (ulong)flags,
                    d2: 0,
                    d3: 0,
                    badge: cap->Badge,
                    isCall: true);

                if (handle == 0 || handle >= 100)
                {
                    return unchecked((ulong)-ENOENT);
                }

                int fd = PosixFdTable.AllocateFd(pcb, PosixFdType.Vfs, (uint)handle, (ushort)flags);
                if (fd < 0)
                {
                    // Table exhausted: close VFS handle
                    FastPathTransfer.Send((Endpoint*)cap->TargetObject, msgInfo: 4, d0: handle, d1: 0, d2: 0, d3: 0, badge: cap->Badge, isCall: true);
                    return unchecked((ulong)-EMFILE);
                }

                return (ulong)fd;
            }
            finally
            {
                FreeBounceFrame(pathPhys);
            }
        }

        private static ulong SysClose(ProcessControlBlock* pcb, CNode* cspaceRoot, int fd)
        {
            PosixFdEntry entry;
            if (!PosixFdTable.GetFd(pcb, fd, out entry))
            {
                return unchecked((ulong)-EBADF);
            }

            if (entry.Type == PosixFdType.Vfs)
            {
                if (cspaceRoot != null)
                {
                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, 11, CapabilityRights.Call | CapabilityRights.Write, out cap);
                    if (status == CSpace.ErrSuccess && cap->Type == CapabilityType.Endpoint)
                    {
                        Cpu.EnableInterrupts();
                        FastPathTransfer.Send(
                            (Endpoint*)cap->TargetObject,
                            msgInfo: 4, // Close method 4
                            d0: (ulong)entry.ServerHandle,
                            d1: 0,
                            d2: 0,
                            d3: 0,
                            badge: cap->Badge,
                            isCall: true);
                    }
                }
                PosixFdTable.FreeFd(pcb, fd);
                return 0;
            }

            if (entry.Type == PosixFdType.Socket)
            {
                if (cspaceRoot != null)
                {
                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, 12, CapabilityRights.Call | CapabilityRights.Write, out cap);
                    if (status == CSpace.ErrSuccess && cap->Type == CapabilityType.Endpoint)
                    {
                        Cpu.EnableInterrupts();
                        FastPathTransfer.Send(
                            (Endpoint*)cap->TargetObject,
                            msgInfo: 11, // Close method 11
                            d0: (ulong)entry.ServerHandle,
                            d1: 0,
                            d2: 0,
                            d3: 0,
                            badge: cap->Badge,
                            isCall: true);
                    }
                }
                PosixFdTable.FreeFd(pcb, fd);
                return 0;
            }

            if (entry.Type == PosixFdType.Stdin || entry.Type == PosixFdType.Stdout || entry.Type == PosixFdType.Stderr)
            {
                PosixFdTable.FreeFd(pcb, fd);
                return 0;
            }

            return unchecked((ulong)-EBADF);
        }

        private static ulong SysMmap(ulong pml4Phys, ulong addr, ulong length, uint prot, uint flags, int fd, ulong offset)
        {
            if (length == 0) return unchecked((ulong)-EINVAL);

            ulong mapped = VirtualMemorySpace.SysMmap(pml4Phys, addr, length, prot, flags);
            if (mapped == ~0UL)
            {
                return unchecked((ulong)-ENOMEM);
            }
            return mapped;
        }

        private static ulong SysBrk(ulong pml4Phys, ulong brk)
        {
            return VirtualMemorySpace.SysBrk(pml4Phys, brk);
        }

        private static ulong SysSocket(ProcessControlBlock* pcb, CNode* cspaceRoot, int domain, int type, int protocol)
        {
            if (cspaceRoot == null) return unchecked((ulong)-EIO);

            Capability* cap;
            ulong status = CSpace.LookupCapability(cspaceRoot, 12, CapabilityRights.Call | CapabilityRights.Write, out cap);
            if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
            {
                return unchecked((ulong)-ENOSYS);
            }

            Cpu.EnableInterrupts();
            ulong sock = FastPathTransfer.Send(
                (Endpoint*)cap->TargetObject,
                msgInfo: 4, // Socket method 4
                d0: (ulong)domain,
                d1: (ulong)type,
                d2: (ulong)protocol,
                d3: 0,
                badge: cap->Badge,
                isCall: true);

            if (sock == 0 || sock == unchecked((ulong)-1))
            {
                return unchecked((ulong)-EIO);
            }

            int fd = PosixFdTable.AllocateFd(pcb, PosixFdType.Socket, (uint)sock, 0);
            if (fd < 0)
            {
                return unchecked((ulong)-EMFILE);
            }

            return (ulong)fd;
        }

        private static ulong SysExit(ThreadControlBlock* current, CNode* cspaceRoot, int code)
        {
            // Only revoke private per-process CNode roots; never wipe the shared
            // kernel or roottask CSpaceRoot inherited by servers and child tasks.
            bool isKernelCNode = Scheduler.MainThread != null && cspaceRoot == Scheduler.MainThread->CSpaceRoot;
            bool isRootCNode = Kernel.Boot.KernelHigh.RoottaskTcb != null && cspaceRoot == Kernel.Boot.KernelHigh.RoottaskTcb->CSpaceRoot;
            if (cspaceRoot != null && !isKernelCNode && !isRootCNode)
            {
                for (uint s = 0; s < CNode.SlotCount; s++)
                {
                    cspaceRoot->Revoke(s);
                }
            }

            Scheduler.TerminateCurrentThread();
            return 0;
        }
    }
}
