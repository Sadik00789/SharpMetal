using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kernel.Arch.x86_64.Descriptors
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GdtPointer
    {
        public ushort Limit;
        public ulong Base;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct GdtTable
    {
        public fixed ulong Entries[7];
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MultiCoreGdtStorage
    {
        public fixed byte RawTables[16 * 56];   // 16 * 56 bytes
        public fixed byte RawPointers[16 * 16]; // 16 * 16 bytes
    }

    public static unsafe class Gdt
    {
        public const ushort KernelCodeSelector = 0x08;
        public const ushort KernelDataSelector = 0x10;
        public const ushort UserDataSelector   = 0x18;
        public const ushort UserCodeSelector   = 0x20;
        public const ushort TssSelector        = 0x28;

        public static GdtTable Table;
        public static GdtPointer Pointer;

        private static MultiCoreGdtStorage s_storage;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GdtTable* GetTable(int coreIndex)
        {
            if (coreIndex < 0 || coreIndex >= 16) return null;
            fixed (byte* p = s_storage.RawTables)
            {
                ulong addr = (ulong)(p + (coreIndex * 56));
                if (addr < Memory.Virtual.Hhdm.Base) addr += Memory.Virtual.Hhdm.Base;
                return (GdtTable*)addr;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GdtPointer* GetPointer(int coreIndex)
        {
            if (coreIndex < 0 || coreIndex >= 16) return null;
            fixed (byte* p = s_storage.RawPointers)
            {
                ulong addr = (ulong)(p + (coreIndex * 16));
                if (addr < Memory.Virtual.Hhdm.Base) addr += Memory.Virtual.Hhdm.Base;
                return (GdtPointer*)addr;
            }
        }

        public static void Initialize(ulong tssBase)
        {
            if (tssBase < Memory.Virtual.Hhdm.Base) tssBase += Memory.Virtual.Hhdm.Base;
            InitializeCore(0, tssBase);
            GdtTable* t0 = GetTable(0);
            GdtPointer* p0 = GetPointer(0);
            if (t0 != null) Table = *t0;
            if (p0 != null)
            {
                Pointer = *p0;
                if (Pointer.Base < Memory.Virtual.Hhdm.Base) Pointer.Base += Memory.Virtual.Hhdm.Base;
            }
        }

        public static void InitializeCore(int coreIndex, ulong tssBase)
        {
            if (coreIndex < 0 || coreIndex >= 16) return;
            if (tssBase < Memory.Virtual.Hhdm.Base) tssBase += Memory.Virtual.Hhdm.Base;

            GdtTable* pTable = GetTable(coreIndex);
            GdtPointer* pPointer = GetPointer(coreIndex);
            if (pTable == null || pPointer == null) return;

            ulong* gdt = pTable->Entries;

            gdt[0] = 0x0000000000000000UL; // 0x00: Null
            gdt[1] = 0x00209A0000000000UL; // 0x08: Kernel Code 64-bit (DPL 0, L=1, P=1, Exec/Read)
            gdt[2] = 0x0000920000000000UL; // 0x10: Kernel Data 64-bit (DPL 0, P=1, Writable)
            gdt[3] = 0x0000F20000000000UL; // 0x18: User Data 64-bit (DPL 3, P=1, Writable)
            gdt[4] = 0x0020FA0000000000UL; // 0x20: User Code 64-bit (DPL 3, L=1, P=1, Exec/Read)

            // Limit = 103 (sizeof(TaskStateSegment) - 1 = 0x67)
            // Type: 0x89 (Present, DPL 0, 64-bit Available TSS)
            ulong tssLimit = 103;
            ulong tssBaseVal = tssBase; // Canonical higher-half virtual address of TSS

            // Slot 5 (0x28): Lower 8 bytes of TSS descriptor
            gdt[5] = (tssLimit & 0xFFFF)
                   | ((tssBaseVal & 0xFFFF) << 16)
                   | (((tssBaseVal >> 16) & 0xFF) << 32)
                   | (0x89UL << 40)                       // Type 0x89: Present 64-bit Available TSS
                   | (((tssLimit >> 16) & 0x0F) << 48)
                   | (((tssBaseVal >> 24) & 0xFF) << 56);

            // Slot 6 (0x30): Upper 8 bytes of TSS descriptor
            gdt[6] = (tssBaseVal >> 32) & 0xFFFFFFFF;    // Base in lower 32 bits, upper 32 bits MUST be 0

            pPointer->Limit = 55;
            ulong gdtAddr = (ulong)gdt;
            if (gdtAddr < Memory.Virtual.Hhdm.Base) gdtAddr += Memory.Virtual.Hhdm.Base;
            pPointer->Base = gdtAddr;
        }
    }
}
