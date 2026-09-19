using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetStack
{
    public static unsafe class Ipv4
    {
        public const byte ProtocolIcmp = 1;
        public const byte ProtocolTcp  = 6;
        public const byte ProtocolUdp  = 17;

        private static ushort s_ipIdCounter = 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort CalculateChecksum(byte* data, int length)
        {
            uint sum = 0;
            ushort* words = (ushort*)data;
            int wordCount = length / 2;
            for (int i = 0; i < wordCount; i++)
            {
                sum += words[i];
            }
            if ((length & 1) != 0)
            {
                sum += data[length - 1];
            }
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            return (ushort)~sum;
        }

        public static void ProcessIpv4Packet(byte* ipPacket, uint length, byte* srcMac)
        {
            if (length < 20) return;

            byte versionAndIhl = ipPacket[0];
            int version = versionAndIhl >> 4;
            int ihl = (versionAndIhl & 0x0F) * 4;
            if (version != 4 || ihl < 20 || length < (uint)ihl) return;

            ushort totalLen = BinaryPrimitives.ReverseEndianness(*(ushort*)(ipPacket + 2));
            if (totalLen > length) totalLen = (ushort)length;

            byte protocol = ipPacket[9];
            uint srcIp = *(uint*)(ipPacket + 12);
            uint dstIp = *(uint*)(ipPacket + 16);

            // Record ARP entry from incoming IP packet
            ArpTable.Update(srcIp, srcMac);

            byte* payload = ipPacket + ihl;
            uint payloadLen = (uint)(totalLen - ihl);

            if (protocol == ProtocolIcmp)
            {
                ProcessIcmp(payload, payloadLen, srcIp, dstIp, srcMac);
            }
            else if (protocol == ProtocolUdp)
            {
                UdpSocket.ProcessPacket(srcIp, dstIp, payload, payloadLen);
            }
            else if (protocol == ProtocolTcp)
            {
                TcpSocket.ProcessPacket(srcIp, dstIp, payload, payloadLen);
            }
        }

        private static void ProcessIcmp(byte* icmpPacket, uint length, uint srcIp, uint dstIp, byte* srcMac)
        {
            if (length < 8) return;

            byte type = icmpPacket[0];
            byte code = icmpPacket[1];

            // Type 8: Echo Request -> reply with Type 0: Echo Reply
            if (type == 8 && code == 0)
            {
                uint frameLen = 14 + 20 + length;
                byte* frame = stackalloc byte[(int)frameLen];

                byte* localMac = stackalloc byte[6];
                NetworkStack.GetLocalMac(localMac);

                // 1. Ethernet Header
                for (int m = 0; m < 6; m++) frame[m] = srcMac[m];
                for (int m = 0; m < 6; m++) frame[6 + m] = localMac[m];
                frame[12] = 0x08; frame[13] = 0x00;

                // 2. IPv4 Header
                byte* ip = frame + 14;
                ip[0] = 0x45; // Version 4, IHL 5 (20 bytes)
                ip[1] = 0x00; // DSCP / ECN
                *(ushort*)(ip + 2) = BinaryPrimitives.ReverseEndianness((ushort)(20 + length));
                *(ushort*)(ip + 4) = BinaryPrimitives.ReverseEndianness(s_ipIdCounter++);
                *(ushort*)(ip + 6) = BinaryPrimitives.ReverseEndianness((ushort)0x4000); // DF
                ip[8] = 64;   // TTL
                ip[9] = ProtocolIcmp;
                *(ushort*)(ip + 10) = 0; // Checksum placeholder
                *(uint*)(ip + 12) = dstIp != 0 ? dstIp : ArpTable.LocalIp;
                *(uint*)(ip + 16) = srcIp;
                *(ushort*)(ip + 10) = CalculateChecksum(ip, 20);

                // 3. ICMP Echo Reply Payload
                byte* icmpReply = frame + 14 + 20;
                for (uint i = 0; i < length; i++) icmpReply[i] = icmpPacket[i];
                icmpReply[0] = 0; // Type = Echo Reply
                icmpReply[1] = 0; // Code = 0
                *(ushort*)(icmpReply + 2) = 0; // Checksum placeholder
                *(ushort*)(icmpReply + 2) = CalculateChecksum(icmpReply, (int)length);

                NetworkStack.SendFrame(frame, frameLen);
            }
        }

        public static void SendIpv4Packet(uint destIp, byte protocol, byte* payload, ushort payloadLen)
        {
            byte* targetMac = stackalloc byte[6];
            if (!ArpTable.Lookup(destIp, targetMac))
            {
                if (!ArpTable.Lookup(ArpTable.GatewayIp, targetMac))
                {
                    // Fallback broadcast if gateway not resolved yet
                    for (int m = 0; m < 6; m++) targetMac[m] = 0xFF;
                }
            }

            uint frameLen = (uint)(14 + 20 + payloadLen);
            byte* frame = stackalloc byte[(int)frameLen];

            byte* localMac = stackalloc byte[6];
            NetworkStack.GetLocalMac(localMac);

            // 1. Ethernet Header
            for (int m = 0; m < 6; m++) frame[m] = targetMac[m];
            for (int m = 0; m < 6; m++) frame[6 + m] = localMac[m];
            frame[12] = 0x08; frame[13] = 0x00;

            // 2. IPv4 Header
            byte* ip = frame + 14;
            ip[0] = 0x45;
            ip[1] = 0x00;
            *(ushort*)(ip + 2) = BinaryPrimitives.ReverseEndianness((ushort)(20 + payloadLen));
            *(ushort*)(ip + 4) = BinaryPrimitives.ReverseEndianness(s_ipIdCounter++);
            *(ushort*)(ip + 6) = BinaryPrimitives.ReverseEndianness((ushort)0x4000); // DF
            ip[8] = 64;
            ip[9] = protocol;
            *(ushort*)(ip + 10) = 0;
            *(uint*)(ip + 12) = ArpTable.LocalIp;
            *(uint*)(ip + 16) = destIp;
            *(ushort*)(ip + 10) = CalculateChecksum(ip, 20);

            // 3. Payload
            byte* dstPayload = frame + 34;
            for (ushort i = 0; i < payloadLen; i++) dstPayload[i] = payload[i];

            NetworkStack.SendFrame(frame, frameLen);
        }
    }
}
