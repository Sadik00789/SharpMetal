using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Descriptors;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Concurrency;
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

        private static SpinLockWithIrqSave s_schedLock;

        public static ThreadControlBlock* CurrentThread
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (ThreadControlBlock*)Cpu.GetCurrentThread();
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => Cpu.SetCurrentThread(value);
        }

        public static ThreadControlBlock* MainThread;
        public static ThreadControlBlock* IdleThread
        {
            get
            {
                int coreIdx = CpuTopology.GetCurrentCoreIndex();
                ThreadControlBlock* t = CpuTopology.GetIdleThread(coreIdx);
                if (t != null) return t;
                return CpuTopology.GetIdleThread(0);
            }
        }

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
            MainThread->Rflags = 0x202;

            CurrentThread = MainThread;
            TaskStateSegment.SetRsp0(MainThread->KernelStackTop);

            // 2. Create BSP Idle thread (TID 1)
            delegate* unmanaged[Cdecl]<void> idleEntry = &IdleLoop;
            ThreadControlBlock* bspIdle = CreateThreadInternal(idleEntry, 3, false);
            CpuTopology.SetIdleThread(0, bspIdle);

            IsRunning = true;
            EarlySerial.WriteLine("[SCHED] Scheduler initialized. Idle and Main threads created.");
        }

        public static void InitializeAp(int coreIndex)
        {
            if (coreIndex < 0 || coreIndex >= CpuTopology.MaxCpus) return;

            delegate* unmanaged[Cdecl]<void> apIdle = &ApIdleLoopThunk;
            ThreadControlBlock* idle = CreateThreadInternal(apIdle, 3, false);
            CpuTopology.SetIdleThread(coreIndex, idle);
            CurrentThread = idle;
            TaskStateSegment.SetRsp0(idle->KernelStackTop);
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
            tcb->IsExecuting = 0;
            tcb->RemainingTicks = GetPriorityTimeslice(priority);
            tcb->IsExecuting = 0;
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
                EnqueueThread(tcb);
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
            tcb->IsExecuting = 0;
            tcb->RemainingTicks = GetPriorityTimeslice(priority);
            tcb->IsExecuting = 0;
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

            // User Thread Stack Synthesis
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

            EnqueueThread(tcb);

            return tcb;
        }

        public static ulong AcquireSchedulerLock()
        {
            return s_schedLock.Acquire();
        }

        public static void ReleaseSchedulerLock(ulong rflags)
        {
            s_schedLock.Release(rflags);
        }

        public static void EnqueueThreadUnlocked(ThreadControlBlock* tcb)
        {
            switch (tcb->Priority)
            {
                case 0: s_q0.Enqueue(tcb); break;
                case 1: s_q1.Enqueue(tcb); break;
                case 2: s_q2.Enqueue(tcb); break;
                default: s_q3.Enqueue(tcb); break;
            }
        }

        private static void EnqueueThread(ThreadControlBlock* tcb)
        {
            ulong rflags = s_schedLock.Acquire();
            try
            {
                EnqueueThreadUnlocked(tcb);
            }
            finally
            {
                s_schedLock.Release(rflags);
            }
        }

        private static ThreadControlBlock* DequeueNextReadyUnlocked()
        {
            if (!s_q0.IsEmpty) return s_q0.Dequeue();
            if (!s_q1.IsEmpty) return s_q1.Dequeue();
            if (!s_q2.IsEmpty) return s_q2.Dequeue();
            if (!s_q3.IsEmpty) return s_q3.Dequeue();
            return null;
        }

        private static ThreadControlBlock* DequeueNextReady()
        {
            ulong rflags = s_schedLock.Acquire();
            try
            {
                return DequeueNextReadyUnlocked();
            }
            finally
            {
                s_schedLock.Release(rflags);
            }
        }

        public static void EnqueueReady(ThreadControlBlock* thread)
        {
            if (thread == null) return;
            thread->State = ThreadState.Ready;
            EnqueueThread(thread);
        }

        public static void DirectHandoff(ThreadControlBlock* target)
        {
            ulong rflags = s_schedLock.Acquire();
            DirectHandoffLocked(target, rflags);
        }

        public static void DirectHandoffLocked(ThreadControlBlock* target, ulong rflags)
        {
            if (target == null || target == CurrentThread)
            {
                s_schedLock.Release(rflags);
                return;
            }

            // Spin until target is not executing on any other core
            while (Concurrency.Atomic.CompareExchange(ref target->IsExecuting, 1, 0) != 0)
            {
                Cpu.Pause();
            }

            ThreadControlBlock* prev = CurrentThread;
            ThreadControlBlock* idle = IdleThread;

            if (prev != null && (prev->State == ThreadState.Running || prev->State == ThreadState.Ready) && prev != idle)
            {
                prev->State = ThreadState.Ready;
                EnqueueThreadUnlocked(prev);
            }

            CurrentThread = target;
            target->State = ThreadState.Running;

            TaskStateSegment.SetRsp0(target->KernelStackTop);

            ulong targetCr3 = target->Pml4Address != 0 ? target->Pml4Address : s_kernelPml4Phys;
            if (targetCr3 != 0 && targetCr3 != Cpu.ReadCr3())
            {
                Cpu.WriteCr3(targetCr3);
            }

            prev->Rflags = rflags;

            Cpu.ContextSwitch(&prev->CurrentRsp, target->CurrentRsp, (prev != null) ? (int*)&prev->IsExecuting : null);

            s_schedLock.Release(CurrentThread->Rflags);
        }

        public static void Yield()
        {
            ulong rflags = s_schedLock.Acquire();
            ThreadControlBlock* curr = CurrentThread;
            ThreadControlBlock* idle = IdleThread;
            if (curr != null && curr->State == ThreadState.Running && curr != idle)
            {
                curr->State = ThreadState.Ready;
                EnqueueThreadUnlocked(curr);
            }
            ScheduleLocked(rflags);
        }

        public static void OnTimerTick()
        {
            TotalTicks++;

            // Periodic priority boost every 100 ticks to prevent starvation
            if (TotalTicks % 100 == 0)
            {
                BoostAllThreads();
            }

            ThreadControlBlock* curr = CurrentThread;
            ThreadControlBlock* idle = IdleThread;
            if (curr != null && curr != idle)
            {
                curr->TotalTicks++;
                curr->RemainingTicks--;

                if (curr->RemainingTicks <= 0)
                {
                    if (curr->Priority < 3)
                    {
                        curr->Priority++;
                    }
                    curr->RemainingTicks = GetPriorityTimeslice(curr->Priority);
                }
            }
        }

        private static void BoostAllThreads()
        {
            ulong rflags = s_schedLock.Acquire();
            try
            {
                PromoteQueue(ref s_q1);
                PromoteQueue(ref s_q2);
                PromoteQueue(ref s_q3);
            }
            finally
            {
                s_schedLock.Release(rflags);
            }

            ThreadControlBlock* curr = CurrentThread;
            if (curr != null)
            {
                curr->Priority = 0;
                curr->RemainingTicks = GetPriorityTimeslice(0);
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
            ulong rflags = s_schedLock.Acquire();
            ScheduleLocked(rflags);
        }

        public static void ScheduleLocked(ulong rflags)
        {
            ThreadControlBlock* next = DequeueNextReadyUnlocked();

            if (next == null)
            {
                ThreadControlBlock* curr = CurrentThread;
                if (curr != null && curr->State == ThreadState.Running)
                {
                    s_schedLock.Release(rflags);
                    return; // Keep running current thread
                }
                next = IdleThread;
            }

            if (next == CurrentThread)
            {
                next->State = ThreadState.Running;
        next->IsExecuting = 1;
                s_schedLock.Release(rflags);
                return;
            }

            // Spin until next thread stack is fully vacated by any other core
            while (Concurrency.Atomic.CompareExchange(ref next->IsExecuting, 1, 0) != 0)
            {
                Cpu.Pause();
            }

            ThreadControlBlock* prev = CurrentThread;
            CurrentThread = next;
            next->State = ThreadState.Running;
        next->IsExecuting = 1;

            // Update TSS.RSP0 to target thread's kernel stack top
            TaskStateSegment.SetRsp0(next->KernelStackTop);

            // Scheduler CR3 Kernel Fallback
            ulong nextCr3 = next->Pml4Address != 0 ? next->Pml4Address : s_kernelPml4Phys;
            if (nextCr3 != 0 && nextCr3 != Cpu.ReadCr3())
            {
                Cpu.WriteCr3(nextCr3);
            }

            prev->Rflags = rflags;

            // Execute assembly context switch
            Cpu.ContextSwitch(&prev->CurrentRsp, next->CurrentRsp, (prev != null) ? (int*)&prev->IsExecuting : null);

            s_schedLock.Release(CurrentThread->Rflags);
        }

        public static void TerminateCurrentThread()
        {
            ulong rflags = s_schedLock.Acquire();
            ThreadControlBlock* curr = CurrentThread;
            if (curr != null)
            {
                curr->State = ThreadState.Dead;
                
                // Release execution guard so other cores don't spin-deadlock or fault
                curr->IsExecuting = 0;

                // Unblock any client waiting for a reply from this dying server thread
                if (curr->ReplyTarget != null)
                {
                    ThreadControlBlock* client = curr->ReplyTarget;
                    if (client->State == ThreadState.BlockedOnReply)
                    {
                        client->IpcRegisters.D0 = unchecked((ulong)-1);
                        client->State = ThreadState.Ready;
                        EnqueueThreadUnlocked(client);
                    }
                    curr->ReplyTarget = null;
                }
                
                curr->BoundEndpoint = null;
                curr->BoundNotification = null;
            }
            ScheduleLocked(rflags);
        }

        public static void UnlockScheduler()
        {
            s_schedLock.Release(0x202);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "ReleaseSchedulerLock")]
        public static void ReleaseSchedulerLock()
        {
            UnlockScheduler();
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void IdleLoop()
        {
            while (true)
            {
                Cpu.EnableInterrupts();
                if (!s_q0.IsEmpty || !s_q1.IsEmpty || !s_q2.IsEmpty || !s_q3.IsEmpty)
                {
                    Schedule();
                }
                else
                {
                    PortIo.IoWait();
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void ApIdleLoopThunk()
        {
            RunApIdleLoop();
        }

        public static void RunApIdleLoop()
        {
            while (true)
            {
                Cpu.EnableInterrupts();
                if (!s_q0.IsEmpty || !s_q1.IsEmpty || !s_q2.IsEmpty || !s_q3.IsEmpty)
                {
                    Schedule();
                }
                else
                {
                    Cpu.Pause();
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "ThreadEntryPointRunner")]
        public static void ThreadEntryPointRunner()
        {
            UnlockScheduler();
            ThreadControlBlock* curr = CurrentThread;
            if (curr != null && curr->EntryPoint != null)
            {
                curr->EntryPoint();
            }
            TerminateCurrentThread();
        }
    }
}
