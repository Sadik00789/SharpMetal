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

        public static void InitializeAp()
        {
            // Access APIC via HHDM mapping
            if (s_apic == null)
            {
                s_apic = (uint*)(Hhdm.Base + ApicBasePhys);
            }

            // Enable APIC via SVR (Spurious vector 0xFF, bit 8 enable)
            WriteRegister(0x0F0, 0x1FF);

            // Clear Task Priority Register to allow all priorities
            WriteRegister(0x080, 0x00);

            // Mask LVT timer on AP until explicitly enabled
            WriteRegister(0x320, 0x00010000);
        }

        public static byte GetId()
        {
            if (s_apic == null)
            {
                s_apic = (uint*)(Hhdm.Base + ApicBasePhys);
            }
            return (byte)((ReadRegister(0x020) >> 24) & 0xFF);
        }

        public static void WaitIcrDelivery()
        {
            // Wait while ICR Bit 12 (Delivery Status) != 0
            while ((ReadRegister(0x300) & (1u << 12)) != 0)
            {
                Cpu.Pause();
            }
        }

        public static void SendInitIpi(byte targetApicId)
        {
            WaitIcrDelivery();
            WriteRegister(0x310, ((uint)targetApicId) << 24);
            // Delivery Mode: 0b101 (INIT), Level: Assert (bit 14 = 1), Trigger: Edge (bit 15 = 0)
            WriteRegister(0x300, 0x00004500);
            WaitIcrDelivery();
        }

        public static void SendInitDeassert(byte targetApicId)
        {
            WaitIcrDelivery();
            WriteRegister(0x310, ((uint)targetApicId) << 24);
            // Delivery Mode: 0b101 (INIT), Level: De-assert (bit 14 = 0), Trigger: Level (bit 15 = 1)
            WriteRegister(0x300, 0x00008500);
            WaitIcrDelivery();
        }

        public static void SendStartupIpi(byte targetApicId, byte vector)
        {
            WaitIcrDelivery();
            WriteRegister(0x310, ((uint)targetApicId) << 24);
            // Delivery Mode: 0b110 (SIPI), Vector: vector
            WriteRegister(0x300, 0x00000600 | (uint)vector);
            WaitIcrDelivery();
        }

        public static void BroadcastIpi(byte vector, bool excludeSelf)
        {
            WaitIcrDelivery();
            uint shorthand = excludeSelf ? 0x00080000u : 0x00040000u; // All Excluding Self vs All Including Self
            // Delivery Mode: Fixed (0), Level: Assert (0x4000), Vector: vector
            WriteRegister(0x300, shorthand | 0x00004000u | (uint)vector);
            WaitIcrDelivery();
        }

        public static void SendIpi(byte targetApicId, byte vector)
        {
            WaitIcrDelivery();
            WriteRegister(0x310, ((uint)targetApicId) << 24);
            WriteRegister(0x300, 0x00004000u | (uint)vector);
            WaitIcrDelivery();
        }

        public static void SendEoi()
        {
            WriteRegister(0x0B0, 0x00);
        }

        public static uint ReadRegister(uint offset)
        {
            return *(uint*)((byte*)s_apic + offset);
        }

        public static void WriteRegister(uint offset, uint value)
        {
            *(uint*)((byte*)s_apic + offset) = value;
        }
    }
}
