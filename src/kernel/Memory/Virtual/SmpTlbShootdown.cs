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

        // Bounded-spin tunables: the ACK wait is always finite with PAUSE
        // backoff so a dead peer can never hang the kernel. The shootdown
        // lock itself is IRQ-safe (cli before ticket acquire); the IPI
        // handler never acquires it, so holding it with IF=0 cannot
        // self-deadlock - peers ACK without needing the lock.
        private const ulong AckTimeoutSpins = 50_000_000;
        private const ulong MaxPagesPerShootdown = 512;

        private static TlbShootdownContext s_context;
        private static SpinLockWithIrqSave s_shootdownLock;

        public static void BroadcastShootdown(ulong pml4Phys, ulong vaddr, ulong pageCount)
        {
            // Fail-closed on degenerate requests.
            if (pageCount == 0) return;
            if (pageCount > MaxPagesPerShootdown) pageCount = MaxPagesPerShootdown;
            // Overflow guard: vaddr + pageCount*4096 must not wrap.
            if (pageCount > 0xFFFFFFFFFFFFFFFFUL / 4096UL) return;
            ulong span = pageCount * 4096UL;
            if (span > 0xFFFFFFFFFFFFFFFFUL - vaddr) return;
            // Only canonical addresses are valid shootdown targets: reject the
            // non-canonical hole between low-half top and HHDM base.
            if (vaddr > 0x00007FFFFFFFFFFFUL && vaddr < Hhdm.Base) return;

            // 1. Invalidate local TLB entry on the executing core
            ulong curCr3 = Cpu.ReadCr3();
            if (curCr3 == pml4Phys || vaddr >= Hhdm.Base)
            {
                for (ulong i = 0; i < pageCount; i++)
                {
                    Cpu.Invlpg(vaddr + (i * 4096));
                }
            }

            // 2. Broadcast TLB shootdown to other cores if SMP is active.
            // AUDIT HARDENING (v1.0.2): deadlock prevention.
            // SpinLockWithIrqSave.Acquire() executes cli BEFORE taking the
            // ticket, so the critical section runs with IF=0 (interrupts
            // explicitly masked while holding the internal shootdown lock).
            // HandleTlbShootdownIpi() never acquires this lock - it only
            // reads the snapshot context, flushes, decrements, and sends
            // EOI - so a holder waiting for ACKs cannot deadlock against a
            // peer waiting for the lock. The ACK spin below is bounded
            // (AckTimeoutSpins with PAUSE + periodic barrier backoff) so a
            // dead peer yields a warning instead of a hang.
            if (CpuTopology.ActiveCoreCount > 1)
            {
                ulong rflags = s_shootdownLock.Acquire();
                try
                {
                    s_context.TargetPml4 = pml4Phys;
                    s_context.VirtualAddress = vaddr;
                    s_context.PageCount = pageCount;
                    s_context.PendingAcks = CpuTopology.ActiveCoreCount - 1;

                    Atomic.MemoryBarrier();

                    // Broadcast IPI vector 0xFE to all excluding self
                    LocalApic.BroadcastIpi(VectorTlbShootdown, excludeSelf: true);

                    // Bounded ACK spin with PAUSE backoff and timeout.
                    ulong timeout = AckTimeoutSpins;
                    while (s_context.PendingAcks > 0 && timeout > 0)
                    {
                        Cpu.Pause();
                        // Periodic barrier so the volatile ACK decrement is observed.
                        if ((timeout & 0xFF) == 0) Atomic.MemoryBarrier();
                        timeout--;
                    }

                    if (timeout == 0 && s_context.PendingAcks > 0)
                    {
                        Kernel.Diagnostics.EarlySerial.WriteLine("[WARN] SMP TLB shootdown ACK timeout; proceeding.");
                    }
                }
                finally
                {
                    s_shootdownLock.Release(rflags);
                }
            }
        }

        public static void HandleTlbShootdownIpi()
        {
            // IPI context: never acquire s_shootdownLock here (would deadlock
            // against a holder spinning with IF=0). Read the snapshot context,
            // flush, decrement, EOI. Bounded page loop prevents a corrupted
            // context from hanging interrupt handling.
            ulong targetPml4 = s_context.TargetPml4;
            ulong targetVaddr = s_context.VirtualAddress;
            ulong count = s_context.PageCount;
            if (count > MaxPagesPerShootdown) count = MaxPagesPerShootdown;
            if (count > 0 && count <= MaxPagesPerShootdown)
            {
                if (count <= 0xFFFFFFFFFFFFFFFFUL / 4096UL)
                {
                    ulong span = count * 4096UL;
                    if (span <= 0xFFFFFFFFFFFFFFFFUL - targetVaddr)
                    {
                        ulong cr3 = Cpu.ReadCr3();
                        if (cr3 == targetPml4 || targetVaddr >= Hhdm.Base)
                        {
                            for (ulong i = 0; i < count; i++)
                            {
                                Cpu.Invlpg(targetVaddr + (i * 4096));
                            }
                        }
                    }
                }
            }

            // Atomically decrement pending acknowledgements counter
            Atomic.Decrement(ref s_context.PendingAcks);

            // Issue End Of Interrupt (EOI) to Local APIC
            LocalApic.SendEoi();
        }
    }
}
