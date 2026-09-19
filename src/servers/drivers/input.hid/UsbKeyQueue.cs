using Userland.Runtime.ZeroAlloc.Interop;

namespace InputHid
{
    /// <summary>
    /// Zero-allocation, fixed-capacity ring buffer that carries translated key
    /// codes injected by the bus.xhci driver into the input service.
    ///
    /// Invariant: both <see cref="Enqueue"/> (via IInputService.InjectKey) and
    /// <see cref="Dequeue"/> (via IInputService.ReadKey) execute on the single
    /// input.hid dispatcher thread, so no locks or atomics are required. Head and
    /// tail are marked volatile defensively. The buffer is an inline fixed-size
    /// array inside a static struct field, so it lives in module static data and
    /// never touches the GC heap.
    /// </summary>
    public static unsafe class UsbKeyQueue
    {
        private const int Capacity = 64;
        private const int Mask = Capacity - 1;

        private struct Ring
        {
            public fixed uint Buffer[Capacity];
            public volatile int Head;
            public volatile int Tail;
        }

        private static Ring s_ring;
        private static bool s_firstInjectReceivedLogged;
        private static bool s_firstInjectedLogged;

        /// <summary>
        /// Appends a key code. When the ring is full the oldest entry is dropped
        /// so the newest keystroke always wins and forward progress is preserved.
        /// </summary>
        public static void Enqueue(uint key)
        {
            int head = s_ring.Head;
            int next = (head + 1) & Mask;

            if (next == s_ring.Tail)
            {
                // Full: drop the oldest entry.
                s_ring.Tail = (s_ring.Tail + 1) & Mask;
            }

            s_ring.Buffer[head] = key;
            s_ring.Head = next;

            if (!s_firstInjectReceivedLogged)
            {
                s_firstInjectReceivedLogged = true;
                SyscallWrappers.Log("[INPUT] USB HID inject accepted.\n");
            }
        }

        /// <summary>Pops the oldest key code, or 0 when the ring is empty.</summary>
        public static uint Dequeue()
        {
            int tail = s_ring.Tail;
            if (tail == s_ring.Head)
            {
                return 0;
            }

            uint key = s_ring.Buffer[tail];
            s_ring.Tail = (tail + 1) & Mask;

            if (!s_firstInjectedLogged)
            {
                s_firstInjectedLogged = true;
                SyscallWrappers.Log("[INPUT] USB HID injected key accepted.\n");
            }

            return key;
        }
    }
}
