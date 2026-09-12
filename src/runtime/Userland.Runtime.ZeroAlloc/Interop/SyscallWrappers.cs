using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Boot;
using Microkernel.Abstractions.Syscalls;

namespace Userland.Runtime.ZeroAlloc.Interop
{
    public static unsafe class SyscallWrappers
    {
        [DllImport("*")]
        public static extern ulong Syscall(ulong num, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Yield()
        {
            return Syscall(SyscallNumbers.SysYield, 0, 0, 0, 0, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetTid()
        {
            return Syscall(SyscallNumbers.SysGetTid, 0, 0, 0, 0, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Log(byte* msg)
        {
            Syscall(SyscallNumbers.SysLog, (ulong)msg, 0, 0, 0, 0, 0);
        }

        public static void Log(string msg)
        {
            byte* buf = stackalloc byte[128];
            int len = msg.Length;
            if (len > 127) len = 127;
            for (int i = 0; i < len; i++)
            {
                buf[i] = (byte)msg[i];
            }
            buf[len] = 0;
            Log(buf);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Exit(ulong code = 0)
        {
            Syscall(SyscallNumbers.SysExit, code, 0, 0, 0, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CreateThread(ulong entryRip, ulong userStackTop, int priority = 0)
        {
            return Syscall(SyscallNumbers.SysCreateThread, entryRip, userStackTop, (ulong)priority, 0, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Send(uint cptr, ulong msgInfo, ulong d0, ulong d1, ulong d2, ulong d3)
        {
            return Syscall(SyscallNumbers.SysSend, cptr, msgInfo, d0, d1, d2, d3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Recv(uint cptr, out ulong msgInfo, out ulong d0, out ulong d1, out ulong d2, out ulong d3)
        {
            ulong* buf = stackalloc ulong[6];
            buf[0] = 0; buf[1] = 0; buf[2] = 0; buf[3] = 0; buf[4] = 0; buf[5] = 0;
            ulong status = Syscall(SyscallNumbers.SysRecv, cptr, (ulong)buf, 0, 0, 0, 0);
            msgInfo = buf[0];
            d0 = buf[1];
            d1 = buf[2];
            d2 = buf[3];
            d3 = buf[4];
            return status;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Call(uint cptr, ulong msgInfo, ulong d0, ulong d1, ulong d2, ulong d3)
        {
            return Syscall(SyscallNumbers.SysCall, cptr, msgInfo, d0, d1, d2, d3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Reply(ulong d0, ulong d1, ulong d2, ulong d3)
        {
            return Syscall(SyscallNumbers.SysReply, d0, d1, d2, d3, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Notify(uint cptr, ulong badge)
        {
            return Syscall(SyscallNumbers.SysNotify, cptr, badge, 0, 0, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong MapMmio(ulong physAddr, ulong virtAddr, ulong sizeBytes, bool writeCombining)
        {
            return Syscall(SyscallNumbers.SysMapMmio, physAddr, virtAddr, sizeBytes, writeCombining ? 1UL : 0UL, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetBootInfo(KernelBootInfo* outInfo)
        {
            return Syscall(SyscallNumbers.SysGetBootInfo, (ulong)outInfo, 0, 0, 0, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CreateProcess(ulong payloadVirt, ulong payloadSize, ulong entryVirt = 0x0000000040000000UL, int priority = 0)
        {
            return Syscall(SyscallNumbers.SysCreateProcess, payloadVirt, payloadSize, entryVirt, (ulong)priority, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetPhysicalAddress(ulong virtAddr)
        {
            return Syscall(SyscallNumbers.SysGetPhysicalAddress, virtAddr, 0, 0, 0, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AllocDma(ulong sizeBytes, ulong virtAddr = 0)
        {
            return Syscall(SyscallNumbers.SysAllocDma, sizeBytes, virtAddr, 0, 0, 0, 0);
        }

        // Architectural correction #1: Ring3/Ring0 boundary — shell never maps
        // pages or inserts TCBs. Kernel creates the address space + stacks + TCB.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Spawn(ulong entryVirt, ulong stackTop)
        {
            return Syscall(SyscallNumbers.SysSpawn, entryVirt, stackTop, 0, 0, 0, 0);
        }
    }
}
