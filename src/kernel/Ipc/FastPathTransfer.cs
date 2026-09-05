using Kernel.Arch.x86_64.Hardware;
using Kernel.Diagnostics;
using Kernel.Scheduling;
using Microkernel.Abstractions.Ipc;

namespace Kernel.Ipc
{
    public static unsafe class FastPathTransfer
    {
        public static ulong Send(
            Endpoint* ep,
            ulong msgInfo,
            ulong d0,
            ulong d1,
            ulong d2,
            ulong d3,
            ulong badge,
            bool isCall)
        {
            if (ep == null) return ~0UL;

            Cpu.DisableInterrupts();
            ThreadControlBlock* caller = Scheduler.CurrentThread;

            if (ep->HasReceivers)
            {
                // Receiver is waiting! Transfer directly into receiver context
                ThreadControlBlock* receiver = ep->DequeueReceive();

                // Mutual Unlinking in sys_recv_any:
                // If receiver was listening on a notification simultaneously, clear waiting thread!
                if (receiver->State == ThreadState.BlockedOnAny)
                {
                    if (receiver->BoundNotification != null)
                    {
                        ((Notification*)receiver->BoundNotification)->WaitingThread = null;
                        receiver->BoundNotification = null;
                    }
                    receiver->BoundEndpoint = null;
                }

                // Copy payload
                receiver->IpcRegisters.D0 = d0;
                receiver->IpcRegisters.D1 = d1;
                receiver->IpcRegisters.D2 = d2;
                receiver->IpcRegisters.D3 = d3;
                receiver->IpcMessageInfo = msgInfo != 0 ? msgInfo : IpcMessageHeader.SyncRpc;
                receiver->IpcBadge = badge;

                // Caller Reply Tracking (User Adjustment 1)
                if (isCall)
                {
                    receiver->ReplyTarget = caller;
                    caller->State = ThreadState.BlockedOnReply;
                }

                receiver->State = ThreadState.Ready;

                // Timeslice Donation: donate caller's remaining ticks directly to receiver
                receiver->RemainingTicks += caller->RemainingTicks;
                caller->RemainingTicks = 0;

                // Direct handoff to receiver
                Scheduler.DirectHandoff(receiver);

                // If this was a call, caller was blocked; when resumed by reply, payload is in caller->IpcRegisters
                ulong ret = isCall ? caller->IpcRegisters.D0 : 0;
                Cpu.EnableInterrupts();
                return ret;
            }
            else
            {
                // No receiver waiting. Caller blocks on send queue
                caller->IpcRegisters.D0 = d0;
                caller->IpcRegisters.D1 = d1;
                caller->IpcRegisters.D2 = d2;
                caller->IpcRegisters.D3 = d3;
                caller->IpcMessageInfo = msgInfo != 0 ? msgInfo : IpcMessageHeader.SyncRpc;
                caller->IpcBadge = badge;

                caller->State = isCall ? ThreadState.BlockedOnReply : ThreadState.BlockedOnSend;
                ep->EnqueueSend(caller);

                Scheduler.Schedule();

                ulong ret = isCall ? caller->IpcRegisters.D0 : 0;
                Cpu.EnableInterrupts();
                return ret;
            }
        }

        public static ulong Recv(
            Endpoint* ep,
            out ulong d0,
            out ulong d1,
            out ulong d2,
            out ulong d3,
            out ulong badge,
            out ulong msgInfo)
        {
            d0 = 0; d1 = 0; d2 = 0; d3 = 0; badge = 0; msgInfo = 0;
            if (ep == null) return ~0UL;

            Cpu.DisableInterrupts();
            ThreadControlBlock* receiver = Scheduler.CurrentThread;

            if (ep->HasSenders)
            {
                // Sender waiting! Transfer payload from sender
                ThreadControlBlock* sender = ep->DequeueSend();

                d0 = sender->IpcRegisters.D0;
                d1 = sender->IpcRegisters.D1;
                d2 = sender->IpcRegisters.D2;
                d3 = sender->IpcRegisters.D3;
                msgInfo = sender->IpcMessageInfo;
                badge = sender->IpcBadge;

                if (sender->State == ThreadState.BlockedOnReply)
                {
                    // Caller did sys_call; bind sender as reply target for this receiver
                    receiver->ReplyTarget = sender;
                }
                else
                {
                    // Regular send; unblock sender
                    sender->State = ThreadState.Ready;
                    Scheduler.EnqueueReady(sender);
                }

                Cpu.EnableInterrupts();
                return 0;
            }
            else
            {
                // No sender waiting; receiver blocks
                receiver->State = ThreadState.BlockedOnReceive;
                ep->EnqueueReceive(receiver);

                Scheduler.Schedule();

                // When resumed, payload is in receiver->IpcRegisters
                d0 = receiver->IpcRegisters.D0;
                d1 = receiver->IpcRegisters.D1;
                d2 = receiver->IpcRegisters.D2;
                d3 = receiver->IpcRegisters.D3;
                msgInfo = receiver->IpcMessageInfo;
                badge = receiver->IpcBadge;

                Cpu.EnableInterrupts();
                return 0;
            }
        }

        public static ulong Reply(ulong d0, ulong d1, ulong d2, ulong d3)
        {
            Cpu.DisableInterrupts();
            ThreadControlBlock* server = Scheduler.CurrentThread;

            ThreadControlBlock* client = server->ReplyTarget;
            if (client == null)
            {
                Cpu.EnableInterrupts();
                return ~0UL; // No client waiting on reply
            }

            server->ReplyTarget = null;

            // Transfer reply payload
            client->IpcRegisters.D0 = d0;
            client->IpcRegisters.D1 = d1;
            client->IpcRegisters.D2 = d2;
            client->IpcRegisters.D3 = d3;
            client->IpcMessageInfo = IpcMessageHeader.Reply;

            client->State = ThreadState.Ready;

            // Timeslice donation back to caller
            client->RemainingTicks += server->RemainingTicks;
            server->RemainingTicks = 0;

            Scheduler.DirectHandoff(client);

            Cpu.EnableInterrupts();
            return 0;
        }
    }
}
