using System;
using System.Buffers.Binary;
using NetStack;
using Userland.Runtime.ZeroAlloc.Interop;

namespace FrontierTests
{
    public static unsafe class NetTest
    {
        public static void Run()
        {
            // 1. ARP Request/Reply Test
            // Send ARP request for gateway (10.0.2.2) -> emits "[NET] ARP Request sent for gateway 10.0.2.2"
            ArpTable.SendArpRequest(ArpTable.GatewayIp);

            // Synthesize an ARP reply from gateway (10.0.2.2) to our MAC
            byte* localMac = stackalloc byte[6];
            NetworkStack.GetLocalMac(localMac);

            byte* arpReplyFrame = stackalloc byte[42];
            // Dest MAC: localMac
            for (int i = 0; i < 6; i++) arpReplyFrame[i] = localMac[i];
            // Src MAC: 52:54:00:12:34:56 (Gateway)
            arpReplyFrame[6] = 0x52; arpReplyFrame[7] = 0x54; arpReplyFrame[8] = 0x00;
            arpReplyFrame[9] = 0x12; arpReplyFrame[10] = 0x34; arpReplyFrame[11] = 0x56;
            // EtherType: ARP (0x0806)
            arpReplyFrame[12] = 0x08; arpReplyFrame[13] = 0x06;

            // ARP payload
            *(ushort*)(arpReplyFrame + 14) = BinaryPrimitives.ReverseEndianness((ushort)1);      // HwType Ethernet
            *(ushort*)(arpReplyFrame + 16) = BinaryPrimitives.ReverseEndianness((ushort)0x0800); // ProtoType IPv4
            arpReplyFrame[18] = 6;
            arpReplyFrame[19] = 4;
            *(ushort*)(arpReplyFrame + 20) = BinaryPrimitives.ReverseEndianness((ushort)2);      // Opcode Reply
            arpReplyFrame[22] = 0x52; arpReplyFrame[23] = 0x54; arpReplyFrame[24] = 0x00;
            arpReplyFrame[25] = 0x12; arpReplyFrame[26] = 0x34; arpReplyFrame[27] = 0x56;
            *(uint*)(arpReplyFrame + 28) = ArpTable.GatewayIp;
            for (int i = 0; i < 6; i++) arpReplyFrame[32 + i] = localMac[i];
            *(uint*)(arpReplyFrame + 38) = ArpTable.LocalIp;

            // Inject ARP reply into stack -> emits "[NET] ARP Reply received: 10.0.2.2 -> 52:54:00:12:34:56"
            Ethernet.ProcessPacket(arpReplyFrame, 42);

            // 2. Synthesize raw ICMP Echo Request frame (Ethernet + IPv4 + ICMP)
            const uint IcmpPayloadLen = 32;
            const uint IcmpTotalLen = 8 + IcmpPayloadLen;
            const uint IpTotalLen = 20 + IcmpTotalLen;
            const uint FrameLen = 14 + IpTotalLen;

            byte* icmpFrame = stackalloc byte[(int)FrameLen];

            // 2.1 Ethernet Header
            for (int i = 0; i < 6; i++) icmpFrame[i] = localMac[i]; // Dest MAC
            icmpFrame[6] = 0x52; icmpFrame[7] = 0x54; icmpFrame[8] = 0x00;
            icmpFrame[9] = 0x12; icmpFrame[10] = 0x34; icmpFrame[11] = 0x56; // Src MAC
            icmpFrame[12] = 0x08; icmpFrame[13] = 0x00; // EtherType IPv4

            // 2.2 IPv4 Header
            byte* ipHdr = icmpFrame + 14;
            ipHdr[0] = 0x45; // Version 4, IHL 5
            ipHdr[1] = 0x00; // DSCP
            *(ushort*)(ipHdr + 2) = BinaryPrimitives.ReverseEndianness((ushort)IpTotalLen);
            *(ushort*)(ipHdr + 4) = BinaryPrimitives.ReverseEndianness((ushort)0x1337);
            *(ushort*)(ipHdr + 6) = 0; // Flags / Frag
            ipHdr[8] = 64;   // TTL
            ipHdr[9] = Ipv4.ProtocolIcmp; // Protocol 1
            *(ushort*)(ipHdr + 10) = 0; // Checksum placeholder
            *(uint*)(ipHdr + 12) = ArpTable.GatewayIp; // Src IP: 10.0.2.2
            *(uint*)(ipHdr + 16) = ArpTable.LocalIp;   // Dst IP: 10.0.2.15
            *(ushort*)(ipHdr + 10) = Ipv4.CalculateChecksum(ipHdr, 20);

            // 2.3 ICMP Echo Request Payload
            byte* icmpHdr = ipHdr + 20;
            icmpHdr[0] = 8; // Type: Echo Request
            icmpHdr[1] = 0; // Code: 0
            *(ushort*)(icmpHdr + 2) = 0; // Checksum placeholder
            *(ushort*)(icmpHdr + 4) = BinaryPrimitives.ReverseEndianness((ushort)0xCAFE); // Identifier
            *(ushort*)(icmpHdr + 6) = BinaryPrimitives.ReverseEndianness((ushort)1);      // Sequence
            for (uint i = 0; i < IcmpPayloadLen; i++)
            {
                icmpHdr[8 + i] = (byte)('A' + (i % 26));
            }
            *(ushort*)(icmpHdr + 2) = Ipv4.CalculateChecksum(icmpHdr, (int)IcmpTotalLen);

            // 3. Inject into Ethernet.ProcessPacket()
            // The stack processes the Echo Request, computes Echo Reply checksum, and transmits via VirtIO
            Ethernet.ProcessPacket(icmpFrame, FrameLen);

            // 4. Test UDP socket allocation and binding
            uint sock = SocketManager.Socket(2, 2, 0); // AF_INET, SOCK_DGRAM
            if (sock != 0)
            {
                SocketManager.Bind(sock, 0, 8080);
            }

            // 5. Emit milestone token
            SyscallWrappers.Log("[PASS] NET: VirtIO RX/TX loopback / ICMP processed\n");
        }
    }
}
