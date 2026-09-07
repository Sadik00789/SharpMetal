using System;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Concurrency;

namespace Kernel.Memory.Virtual
{
    [StructLayout(LayoutKind.Sequential)]
    public struct TlbShootdownContext
    {
        public ulong TargetPml4;
        public ulong VirtualAddress;
        public ulong PageCount;
        public volatile int PendingAcks;
    }

    public static unsafe class SmpTlbShootdown
    {
        public const byte VectorTlbShootdown = 0xFE;

        private static TlbShootdownContext s_context;
        private static TicketSpinLock s_shootdownLock;

        public static void BroadcastShootdown(ulong pml4Phys, ulong vaddr, ulong pageCount)
        {
            // 1. Invalidate local TLB entry on the executing core
            ulong curCr3 = Cpu.ReadCr3();
            if (curCr3 == pml4Phys || vaddr >= Hhdm.Base)
            {
                for (ulong i = 0; i < pageCount; i++)
                {
                    Cpu.Invlpg(vaddr + (i * 4096));
                }
            }

            // 2. Broadcast TLB shootdown to other cores if SMP is active
            if (CpuTopology.ActiveCoreCount > 1)
            {
                // CRITICAL ARCHITECTURAL RULE:
                // Never acquire shootdown lock with interrupts disabled (cli) to avoid deadlocks!
                Cpu.EnableInterrupts();

                s_shootdownLock.Acquire();
                try
                {
                    s_context.TargetPml4 = pml4Phys;
                    s_context.VirtualAddress = vaddr;
                    s_context.PageCount = pageCount;
                    s_context.PendingAcks = CpuTopology.ActiveCoreCount - 1;

                    Atomic.MemoryBarrier();

                    // Broadcast IPI vector 0xFE to all excluding self
                    LocalApic.BroadcastIpi(VectorTlbShootdown, excludeSelf: true);

                    // Spin-wait with pause while keeping interrupts enabled
                    while (s_context.PendingAcks > 0)
                    {
                        Cpu.Pause();
                    }
                }
                finally
                {
                    s_shootdownLock.Release();
                }
            }
        }

        public static void HandleTlbShootdownIpi()
        {
            // Read CR3 and check if this core is running the target address space
            ulong cr3 = Cpu.ReadCr3();
            if (cr3 == s_context.TargetPml4 || s_context.VirtualAddress >= Hhdm.Base)
            {
                for (ulong i = 0; i < s_context.PageCount; i++)
                {
                    Cpu.Invlpg(s_context.VirtualAddress + (i * 4096));
                }
            }

            // Atomically decrement pending acknowledgements counter
            Atomic.Decrement(ref s_context.PendingAcks);

            // Issue End Of Interrupt (EOI) to Local APIC
            LocalApic.SendEoi();
        }
    }
}
