using System;
using System.Runtime.InteropServices;

namespace Kernel.Arch.x86_64.Descriptors
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IdtEntry
    {
        public ushort OffsetLow;      // Bits 0..15 of ISR entry address
        public ushort Selector;       // Code segment selector in GDT (0x08)
        public byte Ist;              // Interrupt Stack Table offset (0..7)
        public byte TypeAttributes;   // Type and attributes (0x8E = 64-bit Interrupt Gate, DPL 0, Present)
        public ushort OffsetMid;      // Bits 16..31 of ISR entry address
        public uint OffsetHigh;       // Bits 32..63 of ISR entry address
        public uint Reserved;         // Reserved, must be zero
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IdtPointer
    {
        public ushort Limit;
        public ulong Base;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 256 * 16)]
    public struct IdtTable
    {
    }

    public static unsafe class Idt
    {
        public const byte TypeInterruptGate = 0x8E; // Present, DPL 0, 64-bit Interrupt Gate
        public const byte TypeTrapGate      = 0x8F; // Present, DPL 0, 64-bit Trap Gate

        public static IdtTable Table;
        public static IdtPointer Pointer;

        public static void Initialize(ulong* thunkTable)
        {
            fixed (IdtTable* pTable = &Table)
            {
                IdtEntry* entries = (IdtEntry*)pTable;

                for (int i = 0; i < 256; i++)
                {
                    ulong isrAddress = thunkTable[i];
                    if (isrAddress < Memory.Virtual.Hhdm.Base)
                    {
                        isrAddress += Memory.Virtual.Hhdm.Base;
                    }

                    entries[i].OffsetLow = (ushort)(isrAddress & 0xFFFFUL);
                    entries[i].Selector = Gdt.KernelCodeSelector; // 0x08
                    entries[i].Ist = 0;
                    entries[i].TypeAttributes = TypeInterruptGate;
                    entries[i].OffsetMid = (ushort)((isrAddress >> 16) & 0xFFFFUL);
                    entries[i].OffsetHigh = (uint)((isrAddress >> 32) & 0xFFFFFFFFUL);
                    entries[i].Reserved = 0;
                }

                Pointer.Limit = (ushort)((256 * sizeof(IdtEntry)) - 1); // 4095 bytes
                ulong baseAddr = (ulong)entries;
                if (baseAddr < Memory.Virtual.Hhdm.Base)
                {
                    baseAddr += Memory.Virtual.Hhdm.Base;
                }
                Pointer.Base = baseAddr;
            }
        }
    }
}
