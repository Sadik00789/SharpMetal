using Kernel.Arch.x86_64.Hardware;
using Kernel.Memory.Heap;
using Kernel.Scheduling;

namespace Kernel.Ipc
{
    public unsafe struct Endpoint
    {
        public ThreadControlBlock* SendHead;
        public ThreadControlBlock* SendTail;

        public ThreadControlBlock* ReceiveHead;
        public ThreadControlBlock* ReceiveTail;

        public static Endpoint* Create()
        {
            Endpoint* ep = (Endpoint*)SlabAllocator.KmAlloc(sizeof(Endpoint) > 32 ? (uint)sizeof(Endpoint) : 32);
            ep->SendHead = null;
            ep->SendTail = null;
            ep->ReceiveHead = null;
            ep->ReceiveTail = null;
            return ep;
        }

        public void EnqueueSend(ThreadControlBlock* tcb)
        {
            tcb->IpcWaitNext = null;
            if (SendTail == null)
            {
                SendHead = tcb;
                SendTail = tcb;
            }
            else
            {
                SendTail->IpcWaitNext = tcb;
                SendTail = tcb;
            }
        }

        public ThreadControlBlock* DequeueSend()
        {
            if (SendHead == null) return null;
            ThreadControlBlock* t = SendHead;
            SendHead = SendHead->IpcWaitNext;
            if (SendHead == null)
            {
                SendTail = null;
            }
            t->IpcWaitNext = null;
            return t;
        }

        public void EnqueueReceive(ThreadControlBlock* tcb)
        {
            tcb->IpcWaitNext = null;
            if (ReceiveTail == null)
            {
                ReceiveHead = tcb;
                ReceiveTail = tcb;
            }
            else
            {
                ReceiveTail->IpcWaitNext = tcb;
                ReceiveTail = tcb;
            }
        }

        public ThreadControlBlock* DequeueReceive()
        {
            if (ReceiveHead == null) return null;
            ThreadControlBlock* t = ReceiveHead;
            ReceiveHead = ReceiveHead->IpcWaitNext;
            if (ReceiveHead == null)
            {
                ReceiveTail = null;
            }
            t->IpcWaitNext = null;
            return t;
        }

        public bool RemoveReceive(ThreadControlBlock* tcb)
        {
            if (ReceiveHead == null || tcb == null) return false;

            if (ReceiveHead == tcb)
            {
                ReceiveHead = tcb->IpcWaitNext;
                if (ReceiveTail == tcb)
                {
                    ReceiveTail = null;
                }
                tcb->IpcWaitNext = null;
                return true;
            }

            ThreadControlBlock* prev = ReceiveHead;
            ThreadControlBlock* curr = prev->IpcWaitNext;

            while (curr != null)
            {
                if (curr == tcb)
                {
                    prev->IpcWaitNext = curr->IpcWaitNext;
                    if (ReceiveTail == tcb)
                    {
                        ReceiveTail = prev;
                    }
                    tcb->IpcWaitNext = null;
                    return true;
                }
                prev = curr;
                curr = curr->IpcWaitNext;
            }

            return false;
        }

        public bool HasSenders => SendHead != null;
        public bool HasReceivers => ReceiveHead != null;
    }
}
