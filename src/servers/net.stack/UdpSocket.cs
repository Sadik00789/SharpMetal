using System;
using System.Buffers.Binary;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetStack
{
    public unsafe struct UdpDatagram
    {
        public uint SrcIp;
        public ushort SrcPort;
        public ushort Length;
        public fixed byte Data[1472];
    }

    public unsafe struct UdpSocketEntry
    {
        public bool InUse;
        public ushort LocalPort;
        public uint LocalIp;
        public ushort RemotePort;
        public uint RemoteIp;

        // Circular buffer of 4 datagrams
        public fixed byte RxBuffer[4 * 1500];
        public int RxHead;
        public int RxTail;
        public int RxCount;
    }

    public unsafe struct UdpSocketStorage
    {
        public fixed byte SocketData[16 * 6144];
    }

    public static unsafe class UdpSocket
    {
        public const int MaxSockets = 16;
        private static UdpSocketStorage s_storage;
        private static ushort s_ephemeralPort = 49152;

        public static UdpSocketEntry* GetSocket(int index)
        {
            fixed (byte* p = s_storage.SocketData)
            {
                return (UdpSocketEntry*)(p + (index * 6144));
            }
        }

        public static ushort AllocateEphemeralPort()
        {
            if (s_ephemeralPort < 49152 || s_ephemeralPort >= 65530)
            {
                s_ephemeralPort = 49152;
            }
            return s_ephemeralPort++;
        }

        public static int AllocateSocket()
        {
            for (int i = 0; i < MaxSockets; i++)
            {
                UdpSocketEntry* s = GetSocket(i);
                if (!s->InUse)
                {
                    s->InUse = true;
                    s->LocalPort = 0;
                    s->LocalIp = 0;
                    s->RemotePort = 0;
                    s->RemoteIp = 0;
                    s->RxHead = 0;
                    s->RxTail = 0;
                    s->RxCount = 0;
                    return i;
                }
            }
            return -1;
        }

        public static bool Bind(int sockId, uint ip, ushort port)
        {
            if (sockId < 0 || sockId >= MaxSockets) return false;
            UdpSocketEntry* s = GetSocket(sockId);
            if (!s->InUse) return false;
            s->LocalIp = ip != 0 ? ip : ArpTable.LocalIp;
            s->LocalPort = port != 0 ? port : AllocateEphemeralPort();
            return true;
        }

        public static uint SendTo(int sockId, uint dstIp, ushort dstPort, byte* data, ushort len)
        {
            if (sockId < 0 || sockId >= MaxSockets || data == null || len == 0) return 0;
            UdpSocketEntry* s = GetSocket(sockId);
            if (!s->InUse) return 0;
            if (s->LocalPort == 0) s->LocalPort = AllocateEphemeralPort();

            ushort totalUdpLen = (ushort)(8 + len);
            byte* udpBuf = stackalloc byte[totalUdpLen];

            *(ushort*)(udpBuf + 0) = BinaryPrimitives.ReverseEndianness(s->LocalPort);
            *(ushort*)(udpBuf + 2) = BinaryPrimitives.ReverseEndianness(dstPort);
            *(ushort*)(udpBuf + 4) = BinaryPrimitives.ReverseEndianness(totalUdpLen);
            *(ushort*)(udpBuf + 6) = 0; // Checksum optional in IPv4

            for (ushort i = 0; i < len; i++)
            {
                udpBuf[8 + i] = data[i];
            }

            Ipv4.SendIpv4Packet(dstIp, Ipv4.ProtocolUdp, udpBuf, totalUdpLen);
            return len;
        }

        public static uint RecvFrom(int sockId, byte* outBuf, uint maxLen)
        {
            if (sockId < 0 || sockId >= MaxSockets || outBuf == null || maxLen == 0) return 0;
            UdpSocketEntry* s = GetSocket(sockId);
            if (!s->InUse || s->RxCount == 0) return 0;

            byte* slotPtr = s->RxBuffer + (s->RxTail * 1500);
            ushort datagramLen = *(ushort*)slotPtr;
            byte* dataPtr = slotPtr + 2;

            uint toCopy = datagramLen < maxLen ? datagramLen : maxLen;
            for (uint i = 0; i < toCopy; i++)
            {
                outBuf[i] = dataPtr[i];
            }

            s->RxTail = (s->RxTail + 1) % 4;
            s->RxCount--;
            return toCopy;
        }

        public static void Close(int sockId)
        {
            if (sockId >= 0 && sockId < MaxSockets)
            {
                UdpSocketEntry* s = GetSocket(sockId);
                s->InUse = false;
                s->RxCount = 0;
            }
        }

        public static void ProcessPacket(uint srcIp, uint dstIp, byte* udpPacket, uint length)
        {
            if (length < 8) return;

            ushort srcPort = BinaryPrimitives.ReverseEndianness(*(ushort*)(udpPacket + 0));
            ushort dstPort = BinaryPrimitives.ReverseEndianness(*(ushort*)(udpPacket + 2));
            ushort udpLen  = BinaryPrimitives.ReverseEndianness(*(ushort*)(udpPacket + 4));

            if (udpLen < 8 || udpLen > length) return;
            uint payloadLen = (uint)(udpLen - 8);
            byte* payload = udpPacket + 8;

            for (int i = 0; i < MaxSockets; i++)
            {
                UdpSocketEntry* s = GetSocket(i);
                if (s->InUse && s->LocalPort == dstPort)
                {
                    if (s->RxCount < 4)
                    {
                        byte* slotPtr = s->RxBuffer + (s->RxHead * 1500);
                        ushort copyLen = payloadLen < 1472 ? (ushort)payloadLen : (ushort)1472;
                        *(ushort*)slotPtr = copyLen;
                        byte* dataPtr = slotPtr + 2;
                        for (ushort b = 0; b < copyLen; b++)
                        {
                            dataPtr[b] = payload[b];
                        }
                        s->RxHead = (s->RxHead + 1) % 4;
                        s->RxCount++;
                    }
                    break;
                }
            }
        }
    }
}
