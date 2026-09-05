using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Diagnostics;

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

            // Unhandled exception report
            EarlySerial.Write("[FAULT] Unhandled Vector: ");
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
