namespace Kernel.Scheduling
{
    public unsafe struct MlfqQueue
    {
        public ThreadControlBlock* Head;
        public ThreadControlBlock* Tail;
        public int Count;

        public void Enqueue(ThreadControlBlock* thread)
        {
            if (thread == null) return;
            thread->Next = null;
            if (Tail == null)
            {
                Head = thread;
                Tail = thread;
            }
            else
            {
                Tail->Next = thread;
                Tail = thread;
            }
            Count++;
        }

        public ThreadControlBlock* Dequeue()
        {
            if (Head == null) return null;
            ThreadControlBlock* t = Head;
            Head = Head->Next;
            if (Head == null)
            {
                Tail = null;
            }
            t->Next = null;
            Count--;
            return t;
        }

        public bool IsEmpty => Head == null;
    }
}
