using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Diagnostics;
using Microkernel.Abstractions.Capabilities;

namespace Kernel.Arch.x86_64.Hardware
{
    [StructLayout(LayoutKind.Sequential)]
    public struct InterruptContext
    {
        // 15 general-purpose registers (pushed by stub in reverse order)
        public ulong Rax;
        public ulong Rcx;
        public ulong Rdx;
        public ulong Rbx;
        public ulong Rbp;
        public ulong Rsi;
        public ulong Rdi;
        public ulong R8;
        public ulong R9;
        public ulong R10;
        public ulong R11;
        public ulong R12;
        public ulong R13;
        public ulong R14;
        public ulong R15;

        // Stub metadata
        public ulong Vector;
        public ulong ErrorCode;

        // CPU hardware frame
        public ulong Rip;
        public ulong Cs;
        public ulong Rflags;
        public ulong Rsp;
        public ulong Ss;
    }

    public unsafe struct VectorNotificationTable
    {
        public fixed ulong NotificationPointers[208];
        public fixed ulong Badges[208];

        public Kernel.Ipc.Notification* this[int index]
        {
            get
            {
                if (index < 0 || index >= 208) return null;
                fixed (ulong* p = NotificationPointers)
                {
                    return (Kernel.Ipc.Notification*)p[index];
                }
            }
            set
            {
                if (index < 0 || index >= 208) return;
                fixed (ulong* p = NotificationPointers)
                {
                    p[index] = (ulong)value;
                }
            }
        }

        public ulong GetBadge(int index)
        {
            if (index < 0 || index >= 208) return 0;
            fixed (ulong* p = Badges)
            {
                return p[index];
            }
        }

        public void SetBadge(int index, ulong badge)
        {
            if (index < 0 || index >= 208) return;
            fixed (ulong* p = Badges)
            {
                p[index] = badge;
            }
        }
    }

    public static unsafe class InterruptDispatcher
    {
        public static volatile int TimerTicks = 0;
        // Phase 2c: panic path never acquires this with IF=1 while holding the
        // scheduler lock; ISR context uses try-acquire semantics via ticket order.
        // Kept as raw ticket lock only for fault re-entry serialization (never
        // taken from timer ISR while scheduler lock is held).
        private static Concurrency.TicketSpinLock s_panicLock;

        public static VectorNotificationTable VectorNotifications;

        public static void RegisterVector(byte vector, Kernel.Ipc.Notification* notif, ulong badge = 0)
        {
            if (vector >= 0x30 && vector <= 0xFE)
            {
                int idx = (int)vector - 0x30;
                VectorNotifications[idx] = notif;
                VectorNotifications.SetBadge(idx, badge != 0 ? badge : (1UL << (idx & 0x3F)));
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "DispatchInterrupt")]
        public static unsafe void DispatchInterrupt(InterruptContext* ctx)
        {
            if (ctx->Vector == 0x20)
            {
                TimerTicks++;
                LocalApic.SendEoi();
                if (Kernel.Scheduling.Scheduler.IsRunning)
                {
                    Kernel.Scheduling.Scheduler.OnTimerTick();
                }
                return;
            }

            if (ctx->Vector == Kernel.Memory.Virtual.SmpTlbShootdown.VectorTlbShootdown)
            {
                Kernel.Memory.Virtual.SmpTlbShootdown.HandleTlbShootdownIpi();
                return;
            }

            if (ctx->Vector >= 0x30 && ctx->Vector <= 0xFD)
            {
                int idx = (int)ctx->Vector - 0x30;
                Kernel.Ipc.Notification* notif = VectorNotifications[idx];
                if (notif != null)
                {
                    ulong badge = VectorNotifications.GetBadge(idx);
                    if (badge == 0) badge = 1UL << (idx & 0x3F);
                    notif->Signal(badge);
                }
                LocalApic.SendEoi();
                return;
            }

            // Check privilege level: Ring 3 (CPL = 3) vs Ring 0 (Kernel)
            if ((ctx->Cs & 3) == 3)
            {
                // Visual Ring 3 Exception Diagnostic on Framebuffer
                if (Kernel.Boot.KernelHigh.GopPhysBase != 0)
                {
                    uint* fb = (uint*)Kernel.Memory.Virtual.Hhdm.PhysicalToVirtual(Kernel.Boot.KernelHigh.GopPhysBase);
                    ulong w = Kernel.Boot.KernelHigh.GopWidth;
                    // Orange Top Banner (scanlines 0..20)
                    for (ulong i = 0; i < w * 20; i++) fb[i] = 0xFFFF5500;
                    // Indicator block: Yellow if #PF (14), White if #GP (13), Cyan if #UD (6), Magenta otherwise
                    uint indColor = (ctx->Vector == 14) ? 0xFFFFFF00 : (ctx->Vector == 13) ? 0xFFFFFFFF : (ctx->Vector == 6) ? 0xFF00FFFF : 0xFFFF00FF;
                    for (ulong y = 25; y < 50; y++) {
                        for (ulong x = 0; x < 150; x++) fb[y * w + x] = indColor;
                    }
                }

                ulong ring3SerRflags = EarlySerial.AcquireLock();
                EarlySerial.WriteInternal("[FAULT] Ring 3 Exception Vector: ");
                EarlySerial.WriteHexInternal(ctx->Vector);
                EarlySerial.WriteInternal(" ErrorCode: ");
                EarlySerial.WriteHexInternal(ctx->ErrorCode);
                EarlySerial.WriteInternal(" RIP: ");
                EarlySerial.WriteHexInternal(ctx->Rip);
                EarlySerial.WriteInternal(" CR2: ");
                EarlySerial.WriteHexInternal(Cpu.ReadCr2());
                EarlySerial.WriteInternal(" Core: ");
                EarlySerial.WriteDecInternal((long)CpuTopology.GetCurrentCoreIndex());
                var currRing3 = Kernel.Scheduling.Scheduler.CurrentThread;
                if (currRing3 != null)
                {
                    EarlySerial.WriteInternal(" TID: ");
                    EarlySerial.WriteDecInternal((long)currRing3->Id);
                }
                EarlySerial.WriteInternal("\r\n");
                EarlySerial.ReleaseLock(ring3SerRflags);

                var current = Kernel.Scheduling.Scheduler.CurrentThread;
                if (current != null)
                {
                    // If roottask (TID 1) faults, system cannot recover: halt CPU so diagnosis is preserved on screen
                    if (current->Id <= 1)
                    {
                        while (true)
                        {
                            Cpu.DisableInterrupts();
                        }
                    }

                    current->State = Kernel.Scheduling.ThreadState.Dead;

                    // If CurrentThread->CSpaceRoot contains a supervisor capability at Slot 8, signal it
                    if (current->CSpaceRoot != null)
                    {
                        Kernel.Capabilities.Capability* cap;
                        if (Kernel.Capabilities.CSpace.LookupCapability(current->CSpaceRoot, 8, CapabilityRights.Write, out cap) == Kernel.Capabilities.CSpace.ErrSuccess)
                        {
                            if (cap->Type == CapabilityType.Notification)
                            {
                                ((Kernel.Ipc.Notification*)cap->TargetObject)->Signal(0xDEAD);
                            }
                        }
                    }

                    // Yield control to the next ready thread
                    Kernel.Scheduling.Scheduler.Schedule();
                    return;
                }
            }

            // Kernel-mode unhandled exception report (halt CPU)
                        // Visual Kernel Panic on Framebuffer
            if (Kernel.Boot.KernelHigh.GopPhysBase != 0)
            {
                uint* fb = (uint*)Kernel.Memory.Virtual.Hhdm.PhysicalToVirtual(Kernel.Boot.KernelHigh.GopPhysBase);
                ulong w = Kernel.Boot.KernelHigh.GopWidth;
                // Bright Red Top Banner (scanlines 0..20)
                for (ulong i = 0; i < w * 20; i++) fb[i] = 0xFFFF0000;
                // Yellow indicator block (width 100px) if vector == 14 (#PF), White if vector == 13 (#GP)
                uint indColor = (ctx->Vector == 14) ? 0xFFFFFF00 : (ctx->Vector == 13) ? 0xFFFFFFFF : 0xFF00FFFF;
                for (ulong y = 25; y < 50; y++) {
                    for (ulong x = 0; x < 150; x++) fb[y * w + x] = indColor;
                }
            }

            EarlySerial.ForceResetLock();
            ulong serRflags = EarlySerial.AcquireLock();

            EarlySerial.WriteInternal("[FAULT] Kernel Panic Vector: ");
            EarlySerial.WriteHexInternal(ctx->Vector);
            EarlySerial.WriteInternal(" ErrorCode: ");
            EarlySerial.WriteHexInternal(ctx->ErrorCode);
            EarlySerial.WriteInternal(" RIP: ");
            EarlySerial.WriteHexInternal(ctx->Rip);
            EarlySerial.WriteInternal(" CS: ");
            EarlySerial.WriteHexInternal(ctx->Cs);
            EarlySerial.WriteInternal(" RSP: ");
            EarlySerial.WriteHexInternal(ctx->Rsp);
            EarlySerial.WriteInternal(" CR2: ");
            EarlySerial.WriteHexInternal(Cpu.ReadCr2());
            EarlySerial.WriteInternal(" Core: ");
            EarlySerial.WriteDecInternal((long)CpuTopology.GetCurrentCoreIndex());
            var curr = Kernel.Scheduling.Scheduler.CurrentThread;
            if (curr != null)
            {
                EarlySerial.WriteInternal(" TID: ");
                EarlySerial.WriteDecInternal((long)curr->Id);
            }
            EarlySerial.WriteInternal("\r\n");

            EarlySerial.WriteInternal("  RAX: "); EarlySerial.WriteHexInternal(ctx->Rax);
            EarlySerial.WriteInternal(" RCX: "); EarlySerial.WriteHexInternal(ctx->Rcx);
            EarlySerial.WriteInternal(" RDX: "); EarlySerial.WriteHexInternal(ctx->Rdx);
            EarlySerial.WriteInternal(" RBX: "); EarlySerial.WriteHexInternal(ctx->Rbx);
            EarlySerial.WriteInternal("\r\n");
            EarlySerial.WriteInternal("  RBP: "); EarlySerial.WriteHexInternal(ctx->Rbp);
            EarlySerial.WriteInternal(" RSI: "); EarlySerial.WriteHexInternal(ctx->Rsi);
            EarlySerial.WriteInternal(" RDI: "); EarlySerial.WriteHexInternal(ctx->Rdi);
            EarlySerial.WriteInternal(" RFLAGS: "); EarlySerial.WriteHexInternal(ctx->Rflags);
            EarlySerial.WriteInternal("\r\n");

            if (ctx->Rsp >= Kernel.Memory.Virtual.Hhdm.Base)
            {
                ulong* stk = (ulong*)ctx->Rsp;
                EarlySerial.WriteInternal("  Stack dump at RSP: ");
                for (int s = 0; s < 8; s++)
                {
                    EarlySerial.WriteHexInternal(stk[s]);
                    EarlySerial.WriteInternal(" ");
                }
                EarlySerial.WriteInternal("\r\n");
            }
            EarlySerial.ReleaseLock(serRflags);

            while (true)
            {
                Cpu.DisableInterrupts();
            }
        }
    }
}
