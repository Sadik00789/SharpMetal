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
        private static ThreadControlBlock* s_zombieList = null;

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
            MainThread = (ThreadControlBlock*)SlabAllocator.KmAlloc((ulong)sizeof(ThreadControlBlock));
            MainThread->Id = NextThreadId++;
            MainThread->State = ThreadState.Running;
            MainThread->Priority = 0;
            MainThread->RemainingTicks = GetPriorityTimeslice(0);
            MainThread->TotalTicks = 0;
            MainThread->IsExecuting = 1;  // BSP main thread starts executing immediately
            MainThread->KernelStackBase = Cpu.GetRsp() & ~4095UL;
            MainThread->KernelStackTop = Cpu.GetRsp() & ~15UL;
            MainThread->CurrentRsp = Cpu.GetRsp() & ~15UL;
            MainThread->EntryPoint = null;
            MainThread->Next = null;

            // Initialize clean FPU/SSE/AVX state
            *(ushort*)&MainThread->FpuState[0] = 0x037F; // FCW
            *(uint*)&MainThread->FpuState[24] = 0x1F80;  // MXCSR
            *(ulong*)&MainThread->FpuState[512] = 7;     // XSTATE_BV (x87 | SSE | AVX)

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

            ThreadControlBlock* tcb = (ThreadControlBlock*)SlabAllocator.KmAlloc((ulong)sizeof(ThreadControlBlock));
            tcb->Id = NextThreadId++;
            tcb->State = ThreadState.Ready;
            tcb->Priority = priority;
            tcb->IsExecuting = 0;
            tcb->RemainingTicks = GetPriorityTimeslice(priority);
            tcb->TotalTicks = 0;
            tcb->EntryPoint = entryPoint;
            tcb->CSpaceRoot = MainThread != null ? MainThread->CSpaceRoot : null;
            tcb->CSpaceRootAddress = (ulong)tcb->CSpaceRoot;
            tcb->Pml4Address = Cpu.ReadCr3();
            tcb->Next = null;

            // Initialize clean FPU/SSE/AVX state
            byte* pFpu = tcb->FpuState;
            for (int i = 0; i < 832; i++) pFpu[i] = 0;
            *(ushort*)&pFpu[0] = 0x037F;
            *(uint*)&pFpu[24] = 0x1F80;
            *(ulong*)&pFpu[512] = 0UL;

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

            // Synthesize initial call frame (8 callee-saved registers + trampoline):
            ulong stackTop = (virtStack + 16384) & ~15UL;
            ulong* sp = (ulong*)(stackTop - 72);
            sp[0] = 0; // r15
            sp[1] = 0; // r14
            sp[2] = 0; // r13
            sp[3] = 0; // r12
            sp[4] = 0; // rsi
            sp[5] = 0; // rdi
            sp[6] = 0; // rbp
            sp[7] = 0; // rbx
            ulong trampoline = Cpu.GetThreadStartTrampoline();
            if (trampoline < Hhdm.Base)
            {
                trampoline += Hhdm.Base;
            }
            sp[8] = trampoline; // Return address popped by ContextSwitch ret

            tcb->CurrentRsp = (ulong)sp;

            if (enqueue)
            {
                EnqueueThread(tcb);
            }

            return tcb;
        }

        public static ThreadControlBlock* CreateUserThread(ulong entryRip, ulong userRsp, int priority = 0, ulong pml4 = 0)
        {
            if (entryRip == 0)
            {
                EarlySerial.WriteLine("[ERROR] Attempted to create thread with null entry RIP!");
                return null;
            }
            if (priority < 0) priority = 0;
            if (priority > 3) priority = 3;

            ThreadControlBlock* tcb = (ThreadControlBlock*)SlabAllocator.KmAlloc((ulong)sizeof(ThreadControlBlock));
            tcb->Id = NextThreadId++;
            tcb->State = ThreadState.Ready;
            tcb->Priority = priority;
            tcb->IsExecuting = 0;
            tcb->RemainingTicks = GetPriorityTimeslice(priority);
            tcb->TotalTicks = 0;
            tcb->EntryPoint = null;
            tcb->Next = null;

            // Initialize clean FPU/SSE/AVX state
            byte* pFpu = tcb->FpuState;
            for (int i = 0; i < 832; i++) pFpu[i] = 0;
            *(ushort*)&pFpu[0] = 0x037F;
            *(uint*)&pFpu[24] = 0x1F80;
            *(ulong*)&pFpu[512] = 0UL;

            ThreadControlBlock* current = CurrentThread;
            tcb->CSpaceRoot = (current != null && current->CSpaceRoot != null) ? current->CSpaceRoot : (MainThread != null ? MainThread->CSpaceRoot : null);
            tcb->CSpaceRootAddress = (ulong)tcb->CSpaceRoot;
            tcb->Pml4Address = pml4 != 0 ? pml4 : (current != null ? current->Pml4Address : s_kernelPml4Phys);

            tcb->IpcWaitNext = null;
            tcb->ReplyTarget = null;
            tcb->BoundEndpoint = null;
            tcb->BoundNotification = null;
            tcb->IpcMessageInfo = 0;
            tcb->IpcBadge = 0;
            tcb->UserRsp = userRsp;

            // Allocate 16 KiB kernel stack
            ulong physStack = PageFrameAllocator.AllocateContiguousFrames(4);
            ulong virtStack = Hhdm.PhysicalToVirtual(physStack);
            tcb->KernelStackBase = virtStack;
            tcb->KernelStackTop = (virtStack + 16384) & ~15UL;

            // User Thread Stack Synthesis (8 callee-saved registers + trampoline + 5 iretq qwords = 14 qwords = 112 bytes)
            ulong stackTop = tcb->KernelStackTop;
            ulong* sp = (ulong*)(stackTop - 112);
            sp[0] = 0; // r15
            sp[1] = 0; // r14
            sp[2] = 0; // r13
            sp[3] = 0; // r12
            sp[4] = 0; // rsi
            sp[5] = 0; // rdi
            sp[6] = 0; // rbp
            sp[7] = 0; // rbx

            ulong trampoline = Cpu.GetUserThreadTrampoline();
            if (trampoline < Hhdm.Base)
            {
                trampoline += Hhdm.Base;
            }
            sp[8] = trampoline; // ContextSwitch ret target

            sp[9]  = entryRip;        // iretq [rsp + 0] : User RIP
            sp[10] = 0x23UL;          // iretq [rsp + 8] : User CS (0x20 | 3)
            sp[11] = 0x3202UL;        // iretq [rsp + 16]: User RFLAGS (IF=1, IOPL=3)
            sp[12] = userRsp & ~15UL; // iretq [rsp + 24]: User RSP (16-byte aligned)
            sp[13] = 0x1BUL;          // iretq [rsp + 32]: User SS (0x18 | 3)

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

            Cpu.ContextSwitch(prev, target);

            s_schedLock.Release(CurrentThread->Rflags);
        }

        public static void YieldCurrentThread() => Yield();

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
            ReapZombies();
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
                // Set IsExecuting = 1 for this thread. This is essential for idle threads
                // which start with IsExecuting=0 and never go through the CAS spin below.
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
            // Note: CompareExchange above already atomically set next->IsExecuting = 1.
            // Do NOT write it again non-atomically here — that would break the memory guard.

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
            Cpu.ContextSwitch(prev, next);

            s_schedLock.Release(CurrentThread->Rflags);
        }

        public static void ReapZombies()
        {
            if (s_zombieList == null) return;

            ulong oldVal = (ulong)s_zombieList;
            fixed (ThreadControlBlock** pList = &s_zombieList)
            {
                ulong* pAtomic = (ulong*)pList;
                while (true)
                {
                    if (oldVal == 0) return;
                    ulong prev = Cpu.AtomicCompareExchange64(pAtomic, 0, oldVal);
                    if (prev == oldVal) break;
                    oldVal = prev;
                }
            }

            ThreadControlBlock* list = (ThreadControlBlock*)oldVal;
            ThreadControlBlock* stillZombie = null;

            while (list != null)
            {
                ThreadControlBlock* next = list->Next;

                // Ensure ReapZombies() explicitly ignores the idle thread and main BSP thread (TID 0 and TID 1)
                // so core execution stacks are never queued for deallocation.
                if (list->Id == 0 || list->Id == 1 || list == MainThread || list == IdleThread)
                {
                    list = next;
                    continue;
                }

                // Only reap if thread has fully vacated CPU cores and is not currently executing
                if (list->IsExecuting == 0 && list != CurrentThread)
                {
                    if (list->KernelStackBase != 0)
                    {
                        ulong phys = Hhdm.VirtualToPhysical(list->KernelStackBase);
                        PageFrameAllocator.FreeContiguousFrames(phys, 4);
                        list->KernelStackBase = 0;
                    }
                    list->State = ThreadState.Dead;
                    SlabAllocator.KmFree(list, (ulong)sizeof(ThreadControlBlock));
                }
                else
                {
                    list->Next = stillZombie;
                    stillZombie = list;
                }

                list = next;
            }

            if (stillZombie != null)
            {
                fixed (ThreadControlBlock** pList = &s_zombieList)
                {
                    ulong* pAtomic = (ulong*)pList;
                    while (true)
                    {
                        ThreadControlBlock* oldHead = s_zombieList;
                        ThreadControlBlock* tail = stillZombie;
                        while (tail->Next != null) tail = tail->Next;
                        tail->Next = oldHead;
                        if ((ThreadControlBlock*)Cpu.AtomicCompareExchange64(pAtomic, (ulong)stillZombie, (ulong)oldHead) == oldHead)
                        {
                            break;
                        }
                    }
                }
            }
        }

        public static void TerminateCurrentThread()
        {
            ulong rflags = s_schedLock.Acquire();
            ThreadControlBlock* curr = CurrentThread;
            if (curr != null)
            {
                // Mark state as Zombie rather than immediately freeing
                curr->State = ThreadState.Zombie;
                
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

                // Queue to zombie list if not TID 0 or TID 1
                if (curr->Id > 1 && curr != MainThread && curr != IdleThread)
                {
                    fixed (ThreadControlBlock** pList = &s_zombieList)
                    {
                        ulong* pAtomic = (ulong*)pList;
                        while (true)
                        {
                            ThreadControlBlock* oldHead = s_zombieList;
                            curr->Next = oldHead;
                            if ((ThreadControlBlock*)Cpu.AtomicCompareExchange64(pAtomic, (ulong)curr, (ulong)oldHead) == oldHead)
                            {
                                break;
                            }
                        }
                    }
                }
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
                ReapZombies();
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
