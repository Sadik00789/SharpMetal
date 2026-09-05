using Kernel.Arch.x86_64.Hardware;
using Kernel.Scheduling;
using Microkernel.Abstractions.Ipc;

namespace Kernel.Ipc
{
    public static unsafe class UnifiedWait
    {
        public static ulong RecvAny(
            Endpoint* ep,
            Notification* notif,
            out ulong msgType,
            out ulong d0,
            out ulong d1,
            out ulong d2,
            out ulong d3,
            out ulong badge)
        {
            msgType = 0; d0 = 0; d1 = 0; d2 = 0; d3 = 0; badge = 0;

            Cpu.DisableInterrupts();
            ThreadControlBlock* current = Scheduler.CurrentThread;

            // 1. Check Notification first
            if (notif != null && notif->State != 0)
            {
                d0 = notif->State;
                notif->State = 0;
                msgType = IpcMessageHeader.AsyncNotification;
                Cpu.EnableInterrupts();
                return 0;
            }

            // 2. Check Endpoint second
            if (ep != null && ep->HasSenders)
            {
                ThreadControlBlock* sender = ep->DequeueSend();

                d0 = sender->IpcRegisters.D0;
                d1 = sender->IpcRegisters.D1;
                d2 = sender->IpcRegisters.D2;
                d3 = sender->IpcRegisters.D3;
                badge = sender->IpcBadge;
                msgType = IpcMessageHeader.SyncRpc;

                if (sender->State == ThreadState.BlockedOnReply)
                {
                    current->ReplyTarget = sender;
                }
                else
                {
                    sender->State = ThreadState.Ready;
                    Scheduler.EnqueueReady(sender);
                }

                Cpu.EnableInterrupts();
                return 0;
            }

            // 3. Sleep on Both: register on both primitives
            current->State = ThreadState.BlockedOnAny;
            current->BoundEndpoint = ep;
            current->BoundNotification = notif;

            if (ep != null)
            {
                ep->EnqueueReceive(current);
            }

            if (notif != null)
            {
                notif->WaitingThread = current;
            }

            Scheduler.Schedule();

            // Ensure interrupts are disabled (cli) during unlinking and state extraction
            Cpu.DisableInterrupts();

            // When unblocked, read delivered payload
            msgType = current->IpcMessageInfo;
            d0 = current->IpcRegisters.D0;
            d1 = current->IpcRegisters.D1;
            d2 = current->IpcRegisters.D2;
            d3 = current->IpcRegisters.D3;
            badge = current->IpcBadge;

            // Mutual Unlinking Safeguard:
            // Ensure listeners are fully detached from the alternate primitive
            if (msgType == IpcMessageHeader.AsyncNotification)
            {
                if (current->BoundEndpoint != null)
                {
                    ((Endpoint*)current->BoundEndpoint)->RemoveReceive(current);
                    current->BoundEndpoint = null;
                }
            }
            else if (msgType == IpcMessageHeader.SyncRpc)
            {
                if (current->BoundNotification != null)
                {
                    ((Notification*)current->BoundNotification)->WaitingThread = null;
                    current->BoundNotification = null;
                }
            }

            current->BoundEndpoint = null;
            current->BoundNotification = null;

            Cpu.EnableInterrupts();
            return 0;
        }
    }
}
