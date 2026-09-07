using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Descriptors;
using Kernel.Concurrency;
using Kernel.Diagnostics;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;
using Kernel.Scheduling;

namespace Kernel.Arch.x86_64.Hardware
{
    public static unsafe class SmpBootstrap
    {
        public const ulong TrampolinePhysBase = 0x00008000UL;
        public const byte TrampolineVector = 0x08; // 0x08 << 12 = 0x8000

        public static void DelayMicroseconds(uint us)
        {
            for (uint i = 0; i < us; i++)
            {
                PortIo.IoWait();
            }
        }

        public static void DelayMilliseconds(uint ms)
        {
            for (uint i = 0; i < ms; i++)
            {
                DelayMicroseconds(1000);
            }
        }

        public static void WakeApplicationProcessors()
        {
            EarlySerial.WriteLine("[SMP] Initiating Application Processor (AP) bootstrap sequence...");

            ulong kernelPml4 = VirtualMemorySpace.Pml4PhysicalAddress != 0 
                ? VirtualMemorySpace.Pml4PhysicalAddress 
                : Cpu.ReadCr3();

            // 1. Stage Trampoline Payload into Physical Address 0x0000_8000
            byte* trampSrc = Cpu.GetApTrampolineBinary();
            ulong trampSize = Cpu.GetApTrampolineBinarySize();
            byte* trampDst = (byte*)Hhdm.PhysicalToVirtual(TrampolinePhysBase);

            for (ulong b = 0; b < trampSize; b++)
            {
                trampDst[b] = trampSrc[b];
            }

            // Write fixed parameters: PML4 at offset 0x08, ApEntry64 at offset 0x10
            *(ulong*)(trampDst + 0x08) = kernelPml4;
            ulong apEntry64 = Cpu.GetApEntry64();
            if (apEntry64 < Hhdm.Base)
            {
                apEntry64 += Hhdm.Base;
            }
            *(ulong*)(trampDst + 0x10) = apEntry64;

            // 2. Pre-allocate execution stacks and configure mailbox for all AP cores
            for (int i = 0; i < CpuTopology.CoreCount; i++)
            {
                CpuCore* core = CpuTopology.GetCore(i);
                if (core == null || core->ApicId == CpuTopology.BspApicId) continue;

                byte apicId = core->ApicId;

                // Allocate 16 KiB 16-byte aligned execution stack (4 physical pages)
                ulong stackPhys = PageFrameAllocator.AllocateContiguousFrames(4);
                ulong stackTop = (Hhdm.PhysicalToVirtual(stackPhys) + 16384) & ~15UL;

                core->StackPointer = stackTop;
                core->Pml4 = kernelPml4;

                ApMailboxEntry* mb = CpuTopology.GetMailbox(i);
                if (mb != null)
                {
                    mb->StackPointer = stackTop;
                    mb->Pml4 = kernelPml4;
                    mb->Status = CpuTopology.BootStatusUnstarted;
                    mb->ApicId = apicId;
                }

                Cpu.SetApInitialStack(apicId, stackTop);
            }

            // 3. Issue INIT-SIPI-SIPI sequence to each AP core
            for (int i = 0; i < CpuTopology.CoreCount; i++)
            {
                CpuCore* core = CpuTopology.GetCore(i);
                if (core == null || core->ApicId == CpuTopology.BspApicId) continue;

                byte apicId = core->ApicId;
                ApMailboxEntry* mb = CpuTopology.GetMailbox(i);
                EarlySerial.Write("[SMP] Booting AP Core ");
                EarlySerial.WriteDec(i);
                EarlySerial.Write(" (APIC ID ");
                EarlySerial.WriteDec(apicId);
                EarlySerial.WriteLine(")...");

                // a. Wait for ICR delivery status clear
                LocalApic.WaitIcrDelivery();

                // b. Send INIT IPI (Assert)
                LocalApic.SendInitIpi(apicId);

                // c. Wait 10ms
                DelayMilliseconds(10);

                // d. Send INIT IPI (De-assert)
                LocalApic.SendInitDeassert(apicId);

                // e. Send first Startup IPI (SIPI) targeting vector 0x08 (0x0000_8000)
                LocalApic.SendStartupIpi(apicId, TrampolineVector);

                // f. Wait 200 microseconds
                DelayMicroseconds(200);

                // g. If target AP has not yet reported online, send second SIPI
                if (mb != null && mb->Status != CpuTopology.BootStatusOnline)
                {
                    LocalApic.SendStartupIpi(apicId, TrampolineVector);
                }

                // h. Poll with 50ms timeout
                int timeoutUs = 50000;
                while (mb != null && mb->Status != CpuTopology.BootStatusOnline && timeoutUs > 0)
                {
                    DelayMicroseconds(100);
                    timeoutUs -= 100;
                }

                if (mb != null && mb->Status == CpuTopology.BootStatusOnline)
                {
                    EarlySerial.Write("[SMP] AP Core ");
                    EarlySerial.WriteDec(i);
                    EarlySerial.WriteLine(" successfully booted and reported online.");
                }
                else
                {
                    EarlySerial.Write("[SMP] AP Core ");
                    EarlySerial.WriteDec(i);
                    EarlySerial.WriteLine(" boot timeout.");
                }
            }

            // 4. Wait for all discovered cores to complete startup
            int waitTimeout = 100000;
            while (CpuTopology.BootedCores < CpuTopology.CoreCount && waitTimeout > 0)
            {
                DelayMicroseconds(50);
                waitTimeout -= 50;
            }

            // 5. Teardown: Unmap low trampoline page from kernel PML4 via SMP broadcast shootdown
            EarlySerial.WriteLine("[SMP] Unmapping low trampoline page 0x0000_8000 via broadcast shootdown...");
            VirtualMemorySpace.UnmapPage(kernelPml4, TrampolinePhysBase);

            EarlySerial.Write("[SMP] ");
            EarlySerial.WriteDec(CpuTopology.BootedCores);
            EarlySerial.WriteLine(" cores synchronized and operational.");
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "ApStartupHandler")]
        public static void ApStartupHandler(ulong apicId)
        {
            int coreIndex = CpuTopology.GetCoreIndex((byte)apicId);
            if (coreIndex < 0 || coreIndex >= CpuTopology.MaxCpus)
            {
                while (true) Cpu.Halt();
            }

            // 1. Allocate dedicated 16 KiB interrupt stack for TSS.RSP0
            ulong intStackPhys = PageFrameAllocator.AllocateContiguousFrames(4);
            ulong intStackTop = (Hhdm.PhysicalToVirtual(intStackPhys) + 16384) & ~15UL;

            PerCpuData* perCpu = CpuTopology.GetPerCpu(coreIndex);
            if (perCpu == null)
            {
                while (true) Cpu.Halt();
            }
            perCpu->CoreIndex = coreIndex;
            perCpu->ApicId = (byte)apicId;
            perCpu->Tss.Initialize(intStackTop);
            perCpu->KernelRsp = intStackTop;
            perCpu->UserRspScratch = 0;

            // 2. Initialize and load core's private GDT & TSS
            ulong tssVirt = (ulong)&perCpu->Tss;
            Gdt.InitializeCore(coreIndex, tssVirt);
            GdtPointer* gdtPtr = Gdt.GetPointer(coreIndex);
            if (gdtPtr != null)
            {
                Cpu.LoadGdt(gdtPtr);
            }
            Cpu.ReloadSegments(0x08, 0x10);
            Cpu.LoadTss(0x28);

            // 3. Configure IA32_GS_BASE (0xC0000101) to point to perCpu
            Cpu.WriteMsr(0xC0000101, (ulong)perCpu);

            // 4. Load shared kernel IDT
            fixed (IdtPointer* ptr = &Idt.Pointer)
            {
                Cpu.LoadIdt(ptr);
            }

            // 5. Enable AVX
            Cpu.EnableAvx();

            // 6. Configure MSRs for SYSCALL/SYSRET
            ulong efer = Cpu.ReadMsr(Cpu.Ia32Efer);
            Cpu.WriteMsr(Cpu.Ia32Efer, efer | 1UL);

            ulong starVal = (0x0013UL << 48) | (0x0008UL << 32);
            Cpu.WriteMsr(Cpu.Ia32Star, starVal);

            ulong lstarVal = Cpu.GetSyscallEntry();
            if (lstarVal < Hhdm.Base)
            {
                lstarVal += Hhdm.Base;
            }
            Cpu.WriteMsr(Cpu.Ia32Lstar, lstarVal);
            Cpu.WriteMsr(Cpu.Ia32Fmask, 0x00000200UL);

            // 7. Initialize AP Local APIC (enable SVR 0x1FF, TPR 0x00)
            LocalApic.InitializeAp();

            // 8. Initialize AP Scheduler Idle Thread
            Scheduler.InitializeAp(coreIndex);

            // 9. Hardened Readiness Ordering:
            // Enable interrupts (sti) and issue memory barrier BEFORE incrementing BootedCores!
            Cpu.EnableInterrupts();
            Atomic.MemoryBarrier();

            // 10. Atomically signal online status
            CpuCore* core = CpuTopology.GetCore(coreIndex);
            if (core != null) core->IsOnline = true;
            ApMailboxEntry* apMb = CpuTopology.GetMailbox(coreIndex);
            if (apMb != null) apMb->Status = CpuTopology.BootStatusOnline;
            Atomic.Increment(ref CpuTopology.BootedCores);

            EarlySerial.Write("[SMP] AP Core ");
            EarlySerial.WriteDec(coreIndex);
            EarlySerial.Write(" (APIC ID ");
            EarlySerial.WriteDec((long)apicId);
            EarlySerial.WriteLine(") online and ready.");

            // 11. Enter AP Idle / Scheduling Loop
            Scheduler.RunApIdleLoop();
        }
    }
}
