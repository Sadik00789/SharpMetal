using System;
using System.Buffers.Binary;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetStack
{
    public static unsafe class Ethernet
    {
        public const ushort EtherTypeIpv4 = 0x0800;
        public const ushort EtherTypeArp  = 0x0806;

        public static void ProcessPacket(byte* frame, uint length)
        {
            if (length < 14) return;

            byte* destMac = frame;
            byte* srcMac = frame + 6;
            ushort etherType = BinaryPrimitives.ReverseEndianness(*(ushort*)(frame + 12));

            // Validate destination MAC: must be for us or broadcast
            bool isForUs = true;
            bool isBroadcast = true;
            byte* localMac = stackalloc byte[6];
            NetworkStack.GetLocalMac(localMac);

            for (int i = 0; i < 6; i++)
            {
                if (destMac[i] != localMac[i]) isForUs = false;
                if (destMac[i] != 0xFF) isBroadcast = false;
            }

            if (!isForUs && !isBroadcast) return;

            byte* payload = frame + 14;
            uint payloadLen = length - 14;

            if (etherType == EtherTypeArp)
            {
                ArpTable.ProcessArpPacket(payload, payloadLen);
            }
            else if (etherType == EtherTypeIpv4)
            {
                Ipv4.ProcessIpv4Packet(payload, payloadLen, srcMac);
            }
        }
    }
}
