using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Descriptors;
using Kernel.Scheduling;

namespace Kernel.Arch.x86_64.Hardware
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PerCpuData
    {
        public int CoreIndex;           // offset 0
        public byte ApicId;             // offset 4
        public byte Reserved;           // offset 5
        public ushort Reserved2;        // offset 6
        public ThreadControlBlock* CurrentThread; // offset 8
        public ulong KernelRsp;         // offset 16 (for syscall stack)
        public ulong UserRspScratch;    // offset 24 (for syscall user rsp)
        public TaskStateSegment Tss;    // offset 32
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    public struct CpuCore
    {
        public byte ApicId;
        public byte ProcessorUid;
        public ushort Reserved;
        public uint Flags;
        public bool IsOnline;
        public bool IsBsp;
        public ushort Reserved2;
        public uint Reserved3;
        public ulong StackPointer;
        public ulong Pml4;
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    public struct ApMailboxEntry
    {
        public ulong StackPointer;
        public ulong Pml4;
        public volatile int Status;     // 0: Unstarted, 1: Booting, 2: Online
        public int Reserved;
        public ulong ApicId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct CpuTopologyStorage
    {
        public fixed ulong PerCpuPointers[16];
        public fixed ulong IdleThreadPointers[16];
        public fixed ulong CoresStorage[16 * 4];     // 16 cores * 32 bytes
        public fixed ulong MailboxStorage[16 * 4];   // 16 entries * 32 bytes
    }

    public static unsafe class CpuTopology
    {
        public const int MaxCpus = 16;
        public const int BootStatusUnstarted = 0;
        public const int BootStatusBooting   = 1;
        public const int BootStatusOnline    = 2;

        public static int CoreCount = 0;
        public static byte BspApicId = 0;

        // Static BSS storage for zero-allocation kernel topology
        private static PerCpuData s_perCpuStorage0;
        private static PerCpuData s_perCpuStorage1;
        private static PerCpuData s_perCpuStorage2;
        private static PerCpuData s_perCpuStorage3;
        private static PerCpuData s_perCpuStorage4;
        private static PerCpuData s_perCpuStorage5;
        private static PerCpuData s_perCpuStorage6;
        private static PerCpuData s_perCpuStorage7;
        private static PerCpuData s_perCpuStorage8;
        private static PerCpuData s_perCpuStorage9;
        private static PerCpuData s_perCpuStorage10;
        private static PerCpuData s_perCpuStorage11;
        private static PerCpuData s_perCpuStorage12;
        private static PerCpuData s_perCpuStorage13;
        private static PerCpuData s_perCpuStorage14;
        private static PerCpuData s_perCpuStorage15;

        private static CpuTopologyStorage s_storage;

        public static volatile int BootedCores = 1; // BSP is core 0

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PerCpuData* GetPerCpu(int index)
        {
            if (index < 0 || index >= MaxCpus) return null;
            fixed (ulong* p = s_storage.PerCpuPointers)
            {
                return (PerCpuData*)p[index];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CpuCore* GetCore(int index)
        {
            if (index < 0 || index >= MaxCpus) return null;
            fixed (ulong* p = s_storage.CoresStorage)
            {
                return (CpuCore*)(p + (index * 4));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ApMailboxEntry* GetMailbox(int index)
        {
            if (index < 0 || index >= MaxCpus) return null;
            fixed (ulong* p = s_storage.MailboxStorage)
            {
                return (ApMailboxEntry*)(p + (index * 4));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ThreadControlBlock* GetIdleThread(int index)
        {
            if (index < 0 || index >= MaxCpus) return null;
            fixed (ulong* p = s_storage.IdleThreadPointers)
            {
                return (ThreadControlBlock*)p[index];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetIdleThread(int index, ThreadControlBlock* thread)
        {
            if (index < 0 || index >= MaxCpus) return;
            fixed (ulong* p = s_storage.IdleThreadPointers)
            {
                p[index] = (ulong)thread;
            }
        }

        public static void InitializeBsp(byte bspApicId)
        {
            BootedCores = 1;
            BspApicId = bspApicId;

            fixed (PerCpuData* p0 = &s_perCpuStorage0)
            fixed (PerCpuData* p1 = &s_perCpuStorage1)
            fixed (PerCpuData* p2 = &s_perCpuStorage2)
            fixed (PerCpuData* p3 = &s_perCpuStorage3)
            fixed (PerCpuData* p4 = &s_perCpuStorage4)
            fixed (PerCpuData* p5 = &s_perCpuStorage5)
            fixed (PerCpuData* p6 = &s_perCpuStorage6)
            fixed (PerCpuData* p7 = &s_perCpuStorage7)
            fixed (PerCpuData* p8 = &s_perCpuStorage8)
            fixed (PerCpuData* p9 = &s_perCpuStorage9)
            fixed (PerCpuData* p10 = &s_perCpuStorage10)
            fixed (PerCpuData* p11 = &s_perCpuStorage11)
            fixed (PerCpuData* p12 = &s_perCpuStorage12)
            fixed (PerCpuData* p13 = &s_perCpuStorage13)
            fixed (PerCpuData* p14 = &s_perCpuStorage14)
            fixed (PerCpuData* p15 = &s_perCpuStorage15)
            fixed (ulong* ptrs = s_storage.PerCpuPointers)
            {
                ptrs[0] = (ulong)p0;
                ptrs[1] = (ulong)p1;
                ptrs[2] = (ulong)p2;
                ptrs[3] = (ulong)p3;
                ptrs[4] = (ulong)p4;
                ptrs[5] = (ulong)p5;
                ptrs[6] = (ulong)p6;
                ptrs[7] = (ulong)p7;
                ptrs[8] = (ulong)p8;
                ptrs[9] = (ulong)p9;
                ptrs[10] = (ulong)p10;
                ptrs[11] = (ulong)p11;
                ptrs[12] = (ulong)p12;
                ptrs[13] = (ulong)p13;
                ptrs[14] = (ulong)p14;
                ptrs[15] = (ulong)p15;

                for (int i = 0; i < MaxCpus; i++)
                {
                    PerCpuData* pc = (PerCpuData*)ptrs[i];
                    pc->CoreIndex = i;
                    pc->ApicId = 0xFF;
                    pc->CurrentThread = null;
                    pc->KernelRsp = 0;
                    pc->UserRspScratch = 0;
                }

                // Setup BSP (Core 0)
                p0->CoreIndex = 0;
                p0->ApicId = bspApicId;

                // Configure BSP IA32_GS_BASE (MSR 0xC0000101)
                Cpu.WriteMsr(0xC0000101, (ulong)p0);
            }
        }

        public static void RegisterCore(byte apicId, byte uid, uint flags)
        {
            if (CoreCount >= MaxCpus) return;

            // Check if already registered
            for (int i = 0; i < CoreCount; i++)
            {
                CpuCore* existing = GetCore(i);
                if (existing != null && existing->ApicId == apicId) return;
            }

            int index = CoreCount++;
            CpuCore* core = GetCore(index);
            if (core != null)
            {
                core->ApicId = apicId;
                core->ProcessorUid = uid;
                core->Flags = flags;
                core->IsBsp = (apicId == BspApicId);
                core->IsOnline = core->IsBsp;
            }

            PerCpuData* pc = GetPerCpu(index);
            if (pc != null)
            {
                pc->ApicId = apicId;
            }
        }

        public static int GetCoreIndex(byte apicId)
        {
            for (int i = 0; i < CoreCount; i++)
            {
                CpuCore* core = GetCore(i);
                if (core != null && core->ApicId == apicId) return i;
            }
            return -1;
        }

        public static int GetCurrentCoreIndex()
        {
            return Cpu.GetCurrentCoreIndex();
        }

        public static int ActiveCoreCount => BootedCores;
    }
}
