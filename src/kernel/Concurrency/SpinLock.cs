using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Hardware;

namespace Kernel.Concurrency
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct TicketSpinLock
    {
        [FieldOffset(0)] public volatile uint Serving;
        [FieldOffset(4)] public volatile uint NextTicket;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Acquire()
        {
            uint ticket = Atomic.FetchAndAdd(ref NextTicket, 1);
            while (Serving != ticket)
            {
                Cpu.Pause();
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release()
        {
            Atomic.MemoryBarrier();
            Serving++;
        }
    }

    public struct SpinLockWithIrqSave
    {
        public TicketSpinLock Lock;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Acquire()
        {
            // AUDIT: interrupts are explicitly masked (cli) BEFORE acquiring
            // the ticket, so the critical section runs with IF=0. Callers must
            // therefore never spin unboundedly while holding this lock when
            // peer cores may need interrupts to ACK (see SmpTlbShootdown).
            ulong rflags = Cpu.ReadRflags();
            Cpu.DisableInterrupts();
            Lock.Acquire();
            return rflags;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release(ulong rflags)
        {
            Lock.Release();
            Cpu.RestoreRflags(rflags);
        }
    }
}
