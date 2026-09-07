using System;
using System.Runtime.InteropServices;
using Kernel.Capabilities;
using Microkernel.Abstractions.Syscalls;

namespace Kernel.Scheduling
{
    public enum ThreadState : int
    {
        Ready = 0,
        Running = 1,
        Blocked = 2,
        Dead = 3,
        BlockedOnSend = 4,
        BlockedOnReceive = 5,
        BlockedOnReply = 6,
        BlockedOnNotification = 7,
        BlockedOnAny = 8
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ThreadControlBlock
    {
        public ulong Id;
        public ulong KernelStackBase;
        public ulong KernelStackTop;
        public ulong CurrentRsp;
        public ThreadState State;
        public int Priority;
        public int RemainingTicks;
        public int TotalTicks;
        public ulong CSpaceRootAddress; // Raw address of CSpaceRoot
        public ulong Pml4Address;
        public delegate* unmanaged[Cdecl]<void> EntryPoint;
        public ThreadControlBlock* Next; // For MLFQ ready queue

        // Phase 5 IPC & Capability fields
        public CNode* CSpaceRoot;
        public ThreadControlBlock* IpcWaitNext; // Intrusive wait queue link
        public ThreadControlBlock* ReplyTarget; // Caller waiting on reply
        public SyscallRegisters IpcRegisters;   // In-transit payload registers
        public ulong IpcMessageInfo;
        public ulong IpcBadge;
        public void* BoundEndpoint;             // For sys_recv_any mutual unlinking
        public void* BoundNotification;         // For sys_recv_any mutual unlinking
        public ulong UserRsp;                   // Phase 6 saved user-space RSP
        public ulong Rflags;                    // Saved RFLAGS for SMP scheduler lock release
        public volatile int IsExecuting;        // SMP concurrent execution guard (1 = executing, 0 = idle)
    }
}
