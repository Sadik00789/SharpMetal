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

    public static class InterruptDispatcher
    {
        public static volatile int TimerTicks = 0;

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

                EarlySerial.Write("[FAULT] Ring 3 Exception Vector: ");
                EarlySerial.WriteHex(ctx->Vector);
                EarlySerial.Write(" ErrorCode: ");
                EarlySerial.WriteHex(ctx->ErrorCode);
                EarlySerial.Write(" RIP: ");
                EarlySerial.WriteHex(ctx->Rip);
                EarlySerial.Write(" CR2: ");
                EarlySerial.WriteHex(Cpu.ReadCr2());
                EarlySerial.WriteLine("");

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

            EarlySerial.Write("[FAULT] Kernel Panic Vector: ");
            EarlySerial.WriteHex(ctx->Vector);
            EarlySerial.Write(" ErrorCode: ");
            EarlySerial.WriteHex(ctx->ErrorCode);
            EarlySerial.Write(" RIP: ");
            EarlySerial.WriteHex(ctx->Rip);
            EarlySerial.Write(" CR2: ");
            EarlySerial.WriteHex(Cpu.ReadCr2());
            EarlySerial.WriteLine("");

            while (true)
            {
                Cpu.DisableInterrupts();
            }
        }
    }
}
