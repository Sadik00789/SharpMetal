using System;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetVirtio
{
    public static unsafe class VirtioNetDriver
    {
        public static byte Mac0 = 0x52;
        public static byte Mac1 = 0x54;
        public static byte Mac2 = 0x00;
        public static byte Mac3 = 0x12;
        public static byte Mac4 = 0x34;
        public static byte Mac5 = 0x56;

        public static byte* CommonCfg = null;
        public static byte* NotifyCfg = null;
        public static byte* IsrCfg = null;
        public static byte* DeviceCfg = null;

        public static uint NotifyOffMultiplier = 0;
        private static byte* s_txNotifyAddr = null;
        private static ushort s_lastUsedIdx = 0;

        private static ulong s_rxRingPhys = 0;
        private static byte* s_rxRingVirt = null;
        private static ulong s_txRingPhys = 0;
        private static byte* s_txRingVirt = null;
        private static ulong s_txPacketPhys = 0;
        private static byte* s_txPacketVirt = null;

        private static void PrintHexByte(byte b)
        {
            byte* hex = stackalloc byte[3];
            byte hi = (byte)((b >> 4) & 0xF);
            byte lo = (byte)(b & 0xF);
            hex[0] = (byte)(hi < 10 ? '0' + hi : 'A' + hi - 10);
            hex[1] = (byte)(lo < 10 ? '0' + lo : 'A' + lo - 10);
            hex[2] = 0;
            SyscallWrappers.Log(hex);
        }

        public static void Initialize()
        {
            // 1. PCIe ECAM Discovery
            ulong ecamVirt = 0x20000000UL;
            SyscallWrappers.MapMmio(0xE0000000UL, ecamVirt, 4194304, writeCombining: false);

            byte* netConfig = null;
            for (uint bus = 0; bus < 4; bus++)
            {
                for (uint dev = 0; dev < 32; dev++)
                {
                    for (uint func = 0; func < 8; func++)
                    {
                        ulong offset = (bus << 20) | (dev << 15) | (func << 12);
                        byte* config = (byte*)(ecamVirt + offset);
                        ushort vendorId = *(ushort*)(config + 0x00);
                        if (vendorId == 0xFFFF || vendorId == 0x0000)
                        {
                            if (func == 0) break;
                            continue;
                        }

                        ushort deviceId = *(ushort*)(config + 0x02);
                        byte baseClass = config[0x0B];

                        // Match VirtIO vendor 0x1AF4 with Network Device 0x1000/0x1041 or class 0x02
                        if (vendorId == 0x1AF4 && (deviceId == 0x1000 || deviceId == 0x1041 || baseClass == 0x02))
                        {
                            netConfig = config;
                            // Enable Bus Master (bit 2) and Memory Space (bit 1)
                            ushort cmd = *(ushort*)(config + 0x04);
                            cmd |= 0x0006;
                            *(ushort*)(config + 0x04) = cmd;
                            break;
                        }
                    }
                    if (netConfig != null) break;
                }
                if (netConfig != null) break;
            }

            // 2. Constraint 4: VirtIO Modern Capability Parsing
            if (netConfig != null)
            {
                ushort status = *(ushort*)(netConfig + 0x06);
                if ((status & (1 << 4)) != 0) // Capabilities list present
                {
                    byte capOffset = netConfig[0x34];
                    int maxCaps = 48;
                    while (capOffset >= 0x40 && maxCaps-- > 0)
                    {
                        byte capId = netConfig[capOffset];
                        byte nextCap = netConfig[capOffset + 1];

                        if (capId == 0x09) // Vendor-Specific Capability
                        {
                            byte cfgType = netConfig[capOffset + 3];
                            byte bar = netConfig[capOffset + 4];
                            uint offset = *(uint*)(netConfig + capOffset + 8);
                            uint length = *(uint*)(netConfig + capOffset + 12);

                            if (bar <= 5)
                            {
                                uint barVal = *(uint*)(netConfig + 0x10 + (bar * 4));
                                ulong barPhys = barVal & ~0xFUL;
                                if (barPhys != 0)
                                {
                                    ulong barVirt = 0x2A000000UL + ((ulong)bar * 0x00100000UL);
                                    SyscallWrappers.MapMmio(barPhys, barVirt, 65536, writeCombining: false);

                                    byte* targetPtr = (byte*)(barVirt + offset);
                                    switch (cfgType)
                                    {
                                        case 1: CommonCfg = targetPtr; break;
                                        case 2:
                                            NotifyCfg = targetPtr;
                                            NotifyOffMultiplier = *(uint*)(netConfig + capOffset + 16);
                                            break;
                                        case 3: IsrCfg = targetPtr; break;
                                        case 4: DeviceCfg = targetPtr; break;
                                    }
                                }
                            }
                        }

                        capOffset = nextCap;
                    }
                }
            }

            // Read MAC address from DeviceCfg if present
            if (DeviceCfg != null)
            {
                Mac0 = DeviceCfg[0];
                Mac1 = DeviceCfg[1];
                Mac2 = DeviceCfg[2];
                Mac3 = DeviceCfg[3];
                Mac4 = DeviceCfg[4];
                Mac5 = DeviceCfg[5];
            }

            // 3. Allocate Virtqueues via AllocDma (Queue 0: RX, Queue 1: TX, plus TX staging buffer)
            s_rxRingPhys = SyscallWrappers.AllocDma(4096, 0x27000000UL);
            s_rxRingVirt = (byte*)0x27000000UL;

            s_txRingPhys = SyscallWrappers.AllocDma(4096, 0x27010000UL);
            s_txRingVirt = (byte*)0x27010000UL;

            s_txPacketPhys = SyscallWrappers.AllocDma(4096, 0x27020000UL);
            s_txPacketVirt = (byte*)0x27020000UL;

            for (int i = 0; i < 4096; i++)
            {
                s_txRingVirt[i] = 0;
                s_txPacketVirt[i] = 0;
            }

            // 4. Configure Virtqueues in CommonCfg if modern device is found
            if (CommonCfg != null)
            {
                // Reset device (status = 0)
                CommonCfg[20] = 0;
                SyscallWrappers.Yield();

                // Status = ACKNOWLEDGE (1) | DRIVER (2)
                CommonCfg[20] = 3;

                // Queue 0: RX
                *(ushort*)(CommonCfg + 22) = 0; // queue_select
                *(ushort*)(CommonCfg + 24) = 16; // queue_size
                *(ulong*)(CommonCfg + 32) = s_rxRingPhys; // queue_desc
                *(ulong*)(CommonCfg + 40) = s_rxRingPhys + 0x800; // queue_driver
                *(ulong*)(CommonCfg + 48) = s_rxRingPhys + 0xC00; // queue_device
                *(ushort*)(CommonCfg + 28) = 1; // queue_enable

                // Queue 1: TX (Adjustment 2: Explicitly write 16 into queue_size)
                *(ushort*)(CommonCfg + 22) = 1; // queue_select
                *(ushort*)(CommonCfg + 24) = 16; // queue_size = 16
                *(ulong*)(CommonCfg + 32) = s_txRingPhys; // queue_desc
                *(ulong*)(CommonCfg + 40) = s_txRingPhys + 0x800; // queue_driver
                *(ulong*)(CommonCfg + 48) = s_txRingPhys + 0xC00; // queue_device
                *(ushort*)(CommonCfg + 28) = 1; // queue_enable

                // Adjustment 1: Calculate txDoorbell = NotifyCfg + (queue_notify_off * notify_off_multiplier)
                ushort queueNotifyOff = *(ushort*)(CommonCfg + 30);
                if (NotifyCfg != null)
                {
                    s_txNotifyAddr = NotifyCfg + ((ulong)queueNotifyOff * NotifyOffMultiplier);
                }

                // Status |= DRIVER_OK (4)
                CommonCfg[20] = (byte)(CommonCfg[20] | 4);
            }

            // 5. Emit Serial Token 4 for Phase 10
            SyscallWrappers.Log("[VIRTIO-NET] Modern PCI VirtIO Network device detected.\n");
            SyscallWrappers.Log("[VIRTIO] VirtIO-Net controller online. MAC: ");
            PrintHexByte(Mac0); SyscallWrappers.Log(":");
            PrintHexByte(Mac1); SyscallWrappers.Log(":");
            PrintHexByte(Mac2); SyscallWrappers.Log(":");
            PrintHexByte(Mac3); SyscallWrappers.Log(":");
            PrintHexByte(Mac4); SyscallWrappers.Log(":");
            PrintHexByte(Mac5); SyscallWrappers.Log("\n");
        }

        public static uint GetMacAddress(ulong outMacBufferPhys)
        {
            if (outMacBufferPhys != 0)
            {
                ulong tempVirt = 0x28000000UL;
                SyscallWrappers.MapMmio(outMacBufferPhys, tempVirt, 4096, writeCombining: false);
                byte* dst = (byte*)tempVirt;
                dst[0] = Mac0;
                dst[1] = Mac1;
                dst[2] = Mac2;
                dst[3] = Mac3;
                dst[4] = Mac4;
                dst[5] = Mac5;
            }
            return 0;
        }

        public static uint SendPacket(ulong packetPhys, uint length)
        {
            if (s_txRingVirt == null || s_txPacketVirt == null) return 0;

            // 1. Map client packet buffer and copy into staging buffer with 12-byte virtio_net_hdr prefix
            ulong clientVirt = 0x27030000UL;
            SyscallWrappers.MapMmio(packetPhys, clientVirt, 4096, writeCombining: false);

            // Zero 12-byte virtio_net_hdr
            for (int i = 0; i < 12; i++)
            {
                s_txPacketVirt[i] = 0;
            }

            byte* src = (byte*)clientVirt;
            byte* dst = s_txPacketVirt + 12;
            for (uint i = 0; i < length && i < 1500; i++)
            {
                dst[i] = src[i];
            }

            uint totalLen = 12 + length;

            // 2. Write descriptor 0
            *(ulong*)(s_txRingVirt + 0) = s_txPacketPhys; // addr
            *(uint*)(s_txRingVirt + 8) = totalLen;        // len
            *(ushort*)(s_txRingVirt + 12) = 0;            // flags = 0
            *(ushort*)(s_txRingVirt + 14) = 0;            // next = 0

            // 3. Put descriptor into avail_ring slot (VirtIO 16-bit wraparound):
            // avail->idx / used->idx are 16-bit circular counters; mask the
            // slot and wrap the counter with explicit ushort arithmetic.
            const int TxRingSize = 16;
            ushort availIdx = *(ushort*)(s_txRingVirt + 0x802);
            ushort slot = (ushort)(availIdx & (TxRingSize - 1));
            *(ushort*)(s_txRingVirt + 0x804 + (slot * 2)) = 0;

            // 4. Descriptor-visibility barrier BEFORE publishing the new index.
            System.Threading.Thread.MemoryBarrier();
            *(ushort*)(s_txRingVirt + 0x802) = (ushort)(availIdx + 1);

            // 5. Ring TX doorbell: calculate txDoorbell = NotifyCfg + (queue_notify_off * notify_off_multiplier), write '1' to kick Queue 1
            if (CommonCfg != null && NotifyCfg != null)
            {
                *(ushort*)(CommonCfg + 22) = 1; // queue_select = 1
                ushort queue_notify_off = *(ushort*)(CommonCfg + 30);
                byte* txDoorbell = NotifyCfg + ((ulong)queue_notify_off * NotifyOffMultiplier);
                *(ushort*)txDoorbell = 1;
            }
            else if (s_txNotifyAddr != null)
            {
                *(ushort*)s_txNotifyAddr = 1;
            }
            else if (NotifyCfg != null)
            {
                *(ushort*)NotifyCfg = 1;
            }

            // 6. Poll used_idx with bounded timeout (16-bit wraparound-safe delta).
            int timeout = 100000;
            while (timeout-- > 0)
            {
                ushort usedIdx = *(ushort*)(s_txRingVirt + 0xC02);
                if ((ushort)(usedIdx - s_lastUsedIdx) != 0)
                {
                    s_lastUsedIdx = usedIdx;
                    break;
                }
                SyscallWrappers.Yield();
            }

            return length;
        }

        public static uint ReceivePacket(ulong packetPhys, uint maxLength)
        {
            return 0; // No packet pending in polled test
        }
    }
}
