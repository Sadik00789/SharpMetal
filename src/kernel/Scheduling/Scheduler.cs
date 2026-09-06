using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Descriptors;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Diagnostics;
using Kernel.Memory.Heap;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;

namespace Kernel.Scheduling
{
    public static unsafe class Scheduler
    {
        public static int GetPriorityTimeslice(int priority)
        {
            switch (priority)
            {
                case 0: return 2;
                case 1: return 4;
                case 2: return 8;
                default: return 16;
            }
        }

        public static ThreadControlBlock* CurrentThread;
        public static ThreadControlBlock* MainThread;
        public static ThreadControlBlock* IdleThread;

        public static ulong NextThreadId = 0;
        public static ulong TotalTicks = 0;
        public static bool IsRunning = false;

        private static MlfqQueue s_q0;
        private static MlfqQueue s_q1;
        private static MlfqQueue s_q2;
        private static MlfqQueue s_q3;

        private static ulong s_kernelPml4Phys;

        public static void SetKernelPml4(ulong pml4) => s_kernelPml4Phys = pml4;

        public static void Initialize()
        {
            s_q0 = default;
            s_q1 = default;
            s_q2 = default;
            s_q3 = default;

            s_kernelPml4Phys = VirtualMemorySpace.Pml4PhysicalAddress != 0 ? VirtualMemorySpace.Pml4PhysicalAddress : Cpu.ReadCr3();

            // 1. Create Main kernel thread (TID 0) representing current execution context
            MainThread = (ThreadControlBlock*)SlabAllocator.KmAlloc(256);
            MainThread->Id = NextThreadId++;
            MainThread->State = ThreadState.Running;
            MainThread->Priority = 0;
            MainThread->RemainingTicks = GetPriorityTimeslice(0);
            MainThread->TotalTicks = 0;
            MainThread->KernelStackBase = Cpu.GetRsp() & ~4095UL;
            MainThread->KernelStackTop = Cpu.GetRsp() & ~15UL;
            MainThread->CurrentRsp = Cpu.GetRsp() & ~15UL;
            MainThread->EntryPoint = null;
            MainThread->Next = null;

            MainThread->CSpaceRoot = null;
            MainThread->CSpaceRootAddress = 0;
            MainThread->IpcWaitNext = null;
            MainThread->ReplyTarget = null;
            MainThread->BoundEndpoint = null;
            MainThread->BoundNotification = null;
            MainThread->IpcMessageInfo = 0;
            MainThread->IpcBadge = 0;

            CurrentThread = MainThread;
            TaskStateSegment.SetRsp0(MainThread->KernelStackTop);

            // 2. Create Idle thread (TID 1)
            delegate* unmanaged[Cdecl]<void> idleEntry = &IdleLoop;
            IdleThread = CreateThreadInternal(idleEntry, 3, false);

            IsRunning = true;
            EarlySerial.WriteLine("[SCHED] Scheduler initialized. Idle and Main threads created.");
        }

        public static ThreadControlBlock* CreateThread(delegate* unmanaged[Cdecl]<void> entryPoint, int priority = 0)
        {
            return CreateThreadInternal(entryPoint, priority, true);
        }

        public static ThreadControlBlock* CreateThread(delegate* unmanaged[Cdecl]<void> entryPoint, int priority, bool enqueue)
        {
            return CreateThreadInternal(entryPoint, priority, enqueue);
        }

        private static ThreadControlBlock* CreateThreadInternal(delegate* unmanaged[Cdecl]<void> entryPoint, int priority, bool enqueue)
        {
            if (priority < 0) priority = 0;
            if (priority > 3) priority = 3;

            ThreadControlBlock* tcb = (ThreadControlBlock*)SlabAllocator.KmAlloc(256);
            tcb->Id = NextThreadId++;
            tcb->State = ThreadState.Ready;
            tcb->Priority = priority;
            tcb->RemainingTicks = GetPriorityTimeslice(priority);
            tcb->TotalTicks = 0;
            tcb->EntryPoint = entryPoint;
            tcb->CSpaceRoot = MainThread != null ? MainThread->CSpaceRoot : null;
            tcb->CSpaceRootAddress = (ulong)tcb->CSpaceRoot;
            tcb->Pml4Address = Cpu.ReadCr3();
            tcb->Next = null;

            tcb->IpcWaitNext = null;
            tcb->ReplyTarget = null;
            tcb->BoundEndpoint = null;
            tcb->BoundNotification = null;
            tcb->IpcMessageInfo = 0;
            tcb->IpcBadge = 0;

            // Allocate 16 KiB kernel stack (4 contiguous physical 4K pages)
            ulong physStack = PageFrameAllocator.AllocateContiguousFrames(4);
            ulong virtStack = Hhdm.PhysicalToVirtual(physStack);
            tcb->KernelStackBase = virtStack;
            tcb->KernelStackTop = (virtStack + 16384) & ~15UL;

            // Synthesize initial call frame:
            ulong stackTop = (virtStack + 16384) & ~15UL;
            ulong* sp = (ulong*)(stackTop - 56);
            sp[0] = 0; // r15
            sp[1] = 0; // r14
            sp[2] = 0; // r13
            sp[3] = 0; // r12
            sp[4] = 0; // rbp
            sp[5] = 0; // rbx
            ulong trampoline = Cpu.GetThreadStartTrampoline();
            if (trampoline < Hhdm.Base)
            {
                trampoline += Hhdm.Base;
            }
            sp[6] = trampoline; // Return address popped by ContextSwitch ret

            tcb->CurrentRsp = (ulong)sp;

            if (enqueue)
            {
                Cpu.DisableInterrupts();
                EnqueueThread(tcb);
                Cpu.EnableInterrupts();
            }

            return tcb;
        }

        public static ThreadControlBlock* CreateUserThread(ulong entryRip, ulong userRsp, int priority = 0, ulong pml4 = 0)
        {
            if (priority < 0) priority = 0;
            if (priority > 3) priority = 3;

            ThreadControlBlock* tcb = (ThreadControlBlock*)SlabAllocator.KmAlloc(256);
            tcb->Id = NextThreadId++;
            tcb->State = ThreadState.Ready;
            tcb->Priority = priority;
            tcb->RemainingTicks = GetPriorityTimeslice(priority);
            tcb->TotalTicks = 0;
            tcb->EntryPoint = null;

            ThreadControlBlock* current = CurrentThread;
            tcb->CSpaceRoot = current != null ? current->CSpaceRoot : null;
            tcb->CSpaceRootAddress = current != null ? current->CSpaceRootAddress : 0;
            tcb->Pml4Address = pml4 != 0 ? pml4 : (current != null ? current->Pml4Address : Cpu.ReadCr3());
            tcb->Next = null;

            tcb->IpcWaitNext = null;
            tcb->ReplyTarget = null;
            tcb->BoundEndpoint = null;
            tcb->BoundNotification = null;
            tcb->IpcMessageInfo = 0;
            tcb->IpcBadge = 0;

            // Allocate 16 KiB kernel stack (4 contiguous physical 4K pages)
            ulong physStack = PageFrameAllocator.AllocateContiguousFrames(4);
            ulong virtStack = Hhdm.PhysicalToVirtual(physStack);
            tcb->KernelStackBase = virtStack;
            tcb->KernelStackTop = (virtStack + 16384) & ~15UL;

            // User Thread Stack Synthesis (Adjustment 3):
            ulong stackTop = tcb->KernelStackTop;
            ulong* sp = (ulong*)(stackTop - 96);
            sp[0] = 0; // r15
            sp[1] = 0; // r14
            sp[2] = 0; // r13
            sp[3] = 0; // r12
            sp[4] = 0; // rbp
            sp[5] = 0; // rbx

            ulong trampoline = Cpu.GetUserThreadTrampoline();
            if (trampoline < Hhdm.Base)
            {
                trampoline += Hhdm.Base;
            }
            sp[6] = trampoline; // ContextSwitch ret target

            sp[7]  = entryRip;        // iretq [rsp + 0] : User RIP
            sp[8]  = 0x23UL;          // iretq [rsp + 8] : User CS (0x20 | 3)
            sp[9]  = 0x3202UL;        // iretq [rsp + 16]: User RFLAGS (IF=1, IOPL=3)
            sp[10] = userRsp & ~15UL; // iretq [rsp + 24]: User RSP (16-byte aligned)
            sp[11] = 0x1BUL;          // iretq [rsp + 32]: User SS (0x18 | 3)

            tcb->CurrentRsp = (ulong)sp;

            ulong rflags = Cpu.ReadRflags();
            Cpu.DisableInterrupts();
            EnqueueThread(tcb);
            Cpu.RestoreRflags(rflags);

            return tcb;
        }

        private static void EnqueueThread(ThreadControlBlock* tcb)
        {
            switch (tcb->Priority)
            {
                case 0: s_q0.Enqueue(tcb); break;
                case 1: s_q1.Enqueue(tcb); break;
                case 2: s_q2.Enqueue(tcb); break;
                default: s_q3.Enqueue(tcb); break;
            }
        }

        private static ThreadControlBlock* DequeueNextReady()
        {
            if (!s_q0.IsEmpty) return s_q0.Dequeue();
            if (!s_q1.IsEmpty) return s_q1.Dequeue();
            if (!s_q2.IsEmpty) return s_q2.Dequeue();
            if (!s_q3.IsEmpty) return s_q3.Dequeue();
            return null;
        }

        public static void EnqueueReady(ThreadControlBlock* thread)
        {
            if (thread == null) return;
            Cpu.DisableInterrupts();
            thread->State = ThreadState.Ready;
            EnqueueThread(thread);
            Cpu.EnableInterrupts();
        }

        public static void DirectHandoff(ThreadControlBlock* target)
        {
            Cpu.DisableInterrupts();

            if (target == null || target == CurrentThread)
            {
                Cpu.EnableInterrupts();
                return;
            }

            ThreadControlBlock* prev = CurrentThread;

            // Direct Handoff Queueing: if donating thread remains in Ready or Running state
            // (e.g. non-blocking sys_send), explicitly re-enqueue it into the MLFQ ready queue
            if (prev != null && (prev->State == ThreadState.Running || prev->State == ThreadState.Ready))
            {
                prev->State = ThreadState.Ready;
                EnqueueThread(prev);
            }

            CurrentThread = target;
            target->State = ThreadState.Running;

            // Update TSS.RSP0 to target thread's kernel stack top
            TaskStateSegment.SetRsp0(target->KernelStackTop);

            // Scheduler CR3 Kernel Fallback: if target->Pml4Address == 0, fall back to kernel master PML4 (s_kernelPml4Phys)
            ulong targetCr3 = target->Pml4Address != 0 ? target->Pml4Address : s_kernelPml4Phys;
            if (targetCr3 != 0 && targetCr3 != Cpu.ReadCr3())
            {
                Cpu.WriteCr3(targetCr3);
            }

            // Execute context switch directly from prev to target
            Cpu.ContextSwitch(&prev->CurrentRsp, target->CurrentRsp);

            Cpu.EnableInterrupts();
        }

        public static void Yield()
        {
            Cpu.DisableInterrupts();
            if (CurrentThread != null && CurrentThread->State == ThreadState.Running)
            {
                CurrentThread->State = ThreadState.Ready;
                EnqueueThread(CurrentThread);
            }
            ScheduleLocked();
            Cpu.EnableInterrupts();
        }

        public static void OnTimerTick()
        {
            TotalTicks++;

            // Periodic priority boost every 100 ticks to prevent starvation
            if (TotalTicks % 100 == 0)
            {
                BoostAllThreads();
            }

            if (CurrentThread != null && CurrentThread != IdleThread)
            {
                CurrentThread->TotalTicks++;
                CurrentThread->RemainingTicks--;

                if (CurrentThread->RemainingTicks <= 0)
                {
                    // Demote priority level on timeslice exhaustion
                    if (CurrentThread->Priority < 3)
                    {
                        CurrentThread->Priority++;
                    }
                    CurrentThread->RemainingTicks = GetPriorityTimeslice(CurrentThread->Priority);

                    if (CurrentThread->State == ThreadState.Running)
                    {
                        // Timeslice expired: do not ContextSwitch inside ISR stack
                        // Thread will yield on next cooperative check
                    }
                }
            }
            else if (CurrentThread == IdleThread)
            {
                // IdleThread yields cooperatively in its own loop
            }
        }

        private static void BoostAllThreads()
        {
            // Promote all threads in Q1, Q2, Q3 to Q0
            PromoteQueue(ref s_q1);
            PromoteQueue(ref s_q2);
            PromoteQueue(ref s_q3);

            if (CurrentThread != null)
            {
                CurrentThread->Priority = 0;
                CurrentThread->RemainingTicks = GetPriorityTimeslice(0);
            }
        }

        private static void PromoteQueue(ref MlfqQueue q)
        {
            while (!q.IsEmpty)
            {
                ThreadControlBlock* t = q.Dequeue();
                t->Priority = 0;
                t->RemainingTicks = GetPriorityTimeslice(0);
                s_q0.Enqueue(t);
            }
        }

        public static void Schedule()
        {
            Cpu.DisableInterrupts();
            ScheduleLocked();
            Cpu.EnableInterrupts();
        }

        private static void ScheduleLocked()
        {
            ThreadControlBlock* next = DequeueNextReady();

            if (next == null)
            {
                if (CurrentThread != null && CurrentThread->State == ThreadState.Running)
                {
                    return; // Keep running current thread
                }
                next = IdleThread;
            }

            if (next == CurrentThread)
            {
                next->State = ThreadState.Running;
                return;
            }

            ThreadControlBlock* prev = CurrentThread;
            CurrentThread = next;
            next->State = ThreadState.Running;

            // Update TSS.RSP0 to target thread's kernel stack top
            TaskStateSegment.SetRsp0(next->KernelStackTop);

            // Scheduler CR3 Kernel Fallback: if next->Pml4Address == 0, fall back to kernel master PML4 (s_kernelPml4Phys)
            ulong nextCr3 = next->Pml4Address != 0 ? next->Pml4Address : s_kernelPml4Phys;
            if (nextCr3 != 0 && nextCr3 != Cpu.ReadCr3())
            {
                Cpu.WriteCr3(nextCr3);
            }

            // Execute assembly context switch
            Cpu.ContextSwitch(&prev->CurrentRsp, next->CurrentRsp);
        }

        public static void TerminateCurrentThread()
        {
            Cpu.DisableInterrupts();
            if (CurrentThread != null)
            {
                CurrentThread->State = ThreadState.Dead;
            }
            ScheduleLocked();
            Cpu.EnableInterrupts();
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void IdleLoop()
        {
            while (true)
            {
                Cpu.EnableInterrupts();
                if (!s_q0.IsEmpty || !s_q1.IsEmpty || !s_q2.IsEmpty || !s_q3.IsEmpty)
                {
                    Yield();
                }
                else
                {
                    PortIo.IoWait();
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "ThreadEntryPointRunner")]
        public static void ThreadEntryPointRunner()
        {
            Cpu.EnableInterrupts();
            if (CurrentThread != null && CurrentThread->EntryPoint != null)
            {
                CurrentThread->EntryPoint();
            }
            TerminateCurrentThread();
        }
    }
}
