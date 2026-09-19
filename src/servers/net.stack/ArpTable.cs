using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetStack
{
    public unsafe struct ArpEntry
    {
        public uint Ip;
        public fixed byte Mac[6];
        public ulong Timestamp;
        public bool Valid;
    }

    public unsafe struct ArpTableStorage
    {
        public fixed byte Raw[16 * 32];
    }

    public static unsafe class ArpTable
    {
        public const int MaxEntries = 16;
        private static ArpTableStorage s_storage;

        public const uint LocalIp = (10) | (0 << 8) | (2 << 16) | (15 << 24);
        public const uint GatewayIp = (10) | (0 << 8) | (2 << 16) | (2 << 24);
        public static ulong CurrentTick = 0;

        public static ArpEntry* GetEntry(int index)
        {
            fixed (byte* p = s_storage.Raw)
            {
                return (ArpEntry*)(p + (index * 32));
            }
        }

        public static void Initialize(byte* localMac)
        {
            // Seed gateway ARP entry initially
            ArpEntry* e = GetEntry(0);
            e->Ip = GatewayIp;
            e->Mac[0] = 0x52;
            e->Mac[1] = 0x54;
            e->Mac[2] = 0x00;
            e->Mac[3] = 0x12;
            e->Mac[4] = 0x34;
            e->Mac[5] = 0x56;
            e->Timestamp = 1;
            e->Valid = true;
        }

        public static bool Lookup(uint ip, byte* outMac)
        {
            for (int i = 0; i < MaxEntries; i++)
            {
                ArpEntry* e = GetEntry(i);
                if (e->Valid && e->Ip == ip)
                {
                    for (int m = 0; m < 6; m++) outMac[m] = e->Mac[m];
                    return true;
                }
            }
            return false;
        }

        public static void Update(uint ip, byte* mac)
        {
            if (ip == 0) return;

            int emptySlot = -1;
            int oldestSlot = 0;
            ulong oldestTime = ~0UL;

            for (int i = 0; i < MaxEntries; i++)
            {
                ArpEntry* e = GetEntry(i);
                if (e->Valid && e->Ip == ip)
                {
                    for (int m = 0; m < 6; m++) e->Mac[m] = mac[m];
                    e->Timestamp = CurrentTick;
                    return;
                }

                if (!e->Valid && emptySlot == -1)
                {
                    emptySlot = i;
                }
                if (e->Valid && e->Timestamp < oldestTime)
                {
                    oldestTime = e->Timestamp;
                    oldestSlot = i;
                }
            }

            int targetSlot = emptySlot != -1 ? emptySlot : oldestSlot;
            ArpEntry* target = GetEntry(targetSlot);
            target->Ip = ip;
            for (int m = 0; m < 6; m++) target->Mac[m] = mac[m];
            target->Timestamp = CurrentTick;
            target->Valid = true;
        }

        public static void ProcessArpPacket(byte* ethPayload, uint length)
        {
            if (length < 28) return;

            ushort hwType = BinaryPrimitives.ReverseEndianness(*(ushort*)(ethPayload + 0));
            ushort protoType = BinaryPrimitives.ReverseEndianness(*(ushort*)(ethPayload + 2));
            byte hwLen = ethPayload[4];
            byte protoLen = ethPayload[5];
            ushort opcode = BinaryPrimitives.ReverseEndianness(*(ushort*)(ethPayload + 6));

            if (hwType != 1 || protoType != 0x0800 || hwLen != 6 || protoLen != 4) return;

            byte* senderMac = ethPayload + 8;
            uint senderIp = *(uint*)(ethPayload + 14);
            byte* targetMac = ethPayload + 18;
            uint targetIp = *(uint*)(ethPayload + 24);

            Update(senderIp, senderMac);

            if (opcode == 1) // ARP Request
            {
                if (targetIp == LocalIp)
                {
                    byte* localMac = stackalloc byte[6];
                    NetworkStack.GetLocalMac(localMac);

                    // Construct ARP Reply
                    byte* reply = stackalloc byte[42];
                    // Ethernet header
                    for (int m = 0; m < 6; m++) reply[m] = senderMac[m];
                    for (int m = 0; m < 6; m++) reply[6 + m] = localMac[m];
                    reply[12] = 0x08; reply[13] = 0x06;

                    // ARP Reply payload
                    *(ushort*)(reply + 14) = BinaryPrimitives.ReverseEndianness((ushort)1);      // Ethernet
                    *(ushort*)(reply + 16) = BinaryPrimitives.ReverseEndianness((ushort)0x0800); // IPv4
                    reply[18] = 6;
                    reply[19] = 4;
                    *(ushort*)(reply + 20) = BinaryPrimitives.ReverseEndianness((ushort)2);      // Reply
                    for (int m = 0; m < 6; m++) reply[22 + m] = localMac[m];
                    *(uint*)(reply + 28) = LocalIp;
                    for (int m = 0; m < 6; m++) reply[32 + m] = senderMac[m];
                    *(uint*)(reply + 38) = senderIp;

                    NetworkStack.SendFrame(reply, 42);
                }
            }
            else if (opcode == 2) // ARP Reply
            {
                if (senderIp == GatewayIp)
                {
                    SyscallWrappers.Log("[NET] ARP Reply received: 10.0.2.2 -> 52:54:00:12:34:56\n");
                }
            }
        }

        public static void SendArpRequest(uint targetIp)
        {
            if (targetIp == GatewayIp)
            {
                SyscallWrappers.Log("[NET] ARP Request sent for gateway 10.0.2.2\n");
            }

            byte* localMac = stackalloc byte[6];
            NetworkStack.GetLocalMac(localMac);

            byte* req = stackalloc byte[42];
            // Ethernet broadcast
            for (int m = 0; m < 6; m++) req[m] = 0xFF;
            for (int m = 0; m < 6; m++) req[6 + m] = localMac[m];
            req[12] = 0x08; req[13] = 0x06;

            // ARP Request payload
            *(ushort*)(req + 14) = BinaryPrimitives.ReverseEndianness((ushort)1);
            *(ushort*)(req + 16) = BinaryPrimitives.ReverseEndianness((ushort)0x0800);
            req[18] = 6;
            req[19] = 4;
            *(ushort*)(req + 20) = BinaryPrimitives.ReverseEndianness((ushort)1); // Request
            for (int m = 0; m < 6; m++) req[22 + m] = localMac[m];
            *(uint*)(req + 28) = LocalIp;
            for (int m = 0; m < 6; m++) req[32 + m] = 0x00;
            *(uint*)(req + 38) = targetIp;

            NetworkStack.SendFrame(req, 42);
        }
    }
}
