using Kernel.Arch.x86_64.Hardware;
using Kernel.Memory.Heap;
using Kernel.Scheduling;
using Microkernel.Abstractions.Ipc;

namespace Kernel.Ipc
{
    public unsafe struct Notification
    {
        public ulong State;
        public ThreadControlBlock* WaitingThread;

        public static Notification* Create()
        {
            Notification* notif = (Notification*)SlabAllocator.KmAlloc(sizeof(Notification) > 32 ? (uint)sizeof(Notification) : 32);
            notif->State = 0;
            notif->WaitingThread = null;
            return notif;
        }

        public void Signal(ulong badge)
        {
            Cpu.DisableInterrupts();

            State |= badge;

            if (WaitingThread != null)
            {
                ThreadControlBlock* waiter = WaitingThread;
                WaitingThread = null;

                ulong mask = State;
                State = 0;

                // Mutual Unlinking in sys_recv_any:
                // If the waiting thread was listening on an endpoint simultaneously, unlink it!
                if (waiter->State == ThreadState.BlockedOnAny)
                {
                    if (waiter->BoundEndpoint != null)
                    {
                        ((Endpoint*)waiter->BoundEndpoint)->RemoveReceive(waiter);
                        waiter->BoundEndpoint = null;
                    }
                    waiter->BoundNotification = null;
                }

                // Deliver payload to waiter's register context
                waiter->IpcRegisters.D0 = mask;
                waiter->IpcMessageInfo = IpcMessageHeader.AsyncNotification;

                // Unblock and enqueue waiter
                Scheduler.EnqueueReady(waiter);
            }

            Cpu.EnableInterrupts();
        }

        public ulong Wait()
        {
            Cpu.DisableInterrupts();

            if (State != 0)
            {
                ulong mask = State;
                State = 0;
                Cpu.EnableInterrupts();
                return mask;
            }

            ThreadControlBlock* current = Scheduler.CurrentThread;
            WaitingThread = current;
            current->State = ThreadState.BlockedOnNotification;

            Scheduler.Schedule();

            // When resumed, payload is in current->IpcRegisters.D0
            ulong deliveredMask = current->IpcRegisters.D0;
            Cpu.EnableInterrupts();
            return deliveredMask;
        }
    }
}
