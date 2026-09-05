using System;
using Kernel.Memory.Virtual;

namespace Kernel.Arch.x86_64.Hardware
{
    public static unsafe class LocalApic
    {
        private const ulong ApicBasePhys = 0xFEE00000;
        private static uint* s_apic;

        public static void Initialize()
        {
            // Access APIC via HHDM mapping
            s_apic = (uint*)(Hhdm.Base + ApicBasePhys);

            // 1. Spurious Interrupt Vector Register (0x0F0):
            // Bit 8 = 1 (APIC Software Enable), Vector = 0xFF
            WriteRegister(0x0F0, 0x1FF);

            // 2. Task Priority Register (0x080): Allow all interrupt priorities
            WriteRegister(0x080, 0x00);

            // 3. Divide Configuration Register (0x3E0): Value 0x03 = Divide by 16
            WriteRegister(0x3E0, 0x03);

            // 4. LVT Timer Register (0x320):
            // Bit 17 = 1 (Periodic Mode), Bit 16 = 0 (Unmasked), Vector = 0x20
            WriteRegister(0x320, 0x00020020);

            // 5. Initial Count Register (0x380): Fast deterministic periodic ticks in QEMU
            WriteRegister(0x380, 0x100000);
        }

        public static void SendEoi()
        {
            WriteRegister(0x0B0, 0x00);
        }

        private static void WriteRegister(uint offset, uint value)
        {
            *(uint*)((byte*)s_apic + offset) = value;
        }
    }
}
