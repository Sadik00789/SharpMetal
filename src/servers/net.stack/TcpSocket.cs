using System;
using System.Buffers.Binary;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetStack
{
    public enum TcpState
    {
        Closed       = 0,
        Listen       = 1,
        SynSent      = 2,
        SynReceived  = 3,
        Established  = 4,
        FinWait1     = 5,
        FinWait2     = 6,
        CloseWait    = 7,
        Closing      = 8,
        LastAck      = 9,
        TimeWait     = 10
    }

    public static class TcpFlags
    {
        public const byte Fin = 0x01;
        public const byte Syn = 0x02;
        public const byte Rst = 0x04;
        public const byte Psh = 0x08;
        public const byte Ack = 0x10;
        public const byte Urg = 0x20;
    }

    public unsafe struct TcpControlBlock
    {
        public bool InUse;
        public TcpState State;
        public ushort LocalPort;
        public uint LocalIp;
        public ushort RemotePort;
        public uint RemoteIp;
        public uint SeqNum;
        public uint AckNum;
        public ushort WindowSize;
        public ulong RtoTick;

        // Circular RX stream buffer
        public fixed byte RxBuffer[4096];
        public int RxHead;
        public int RxTail;
        public int RxCount;
    }

    public unsafe struct TcpSocketStorage
    {
        public fixed byte TcbData[16 * 4160];
        public fixed byte TimerWheel[256];
    }

    public static unsafe class TcpSocket
    {
        public const int MaxSockets = 16;
        private static TcpSocketStorage s_storage;
        private static ushort s_ephemeralPort = 55000;

        public static TcpControlBlock* GetTcb(int index)
        {
            fixed (byte* p = s_storage.TcbData)
            {
                return (TcpControlBlock*)(p + (index * 4160));
            }
        }

        public static ushort AllocateEphemeralPort()
        {
            if (s_ephemeralPort < 50000 || s_ephemeralPort >= 65530)
            {
                s_ephemeralPort = 50000;
            }
            return s_ephemeralPort++;
        }

        public static int AllocateSocket()
        {
            for (int i = 0; i < MaxSockets; i++)
            {
                TcpControlBlock* tcb = GetTcb(i);
                if (!tcb->InUse)
                {
                    tcb->InUse = true;
                    tcb->State = TcpState.Closed;
                    tcb->LocalPort = 0;
                    tcb->LocalIp = 0;
                    tcb->RemotePort = 0;
                    tcb->RemoteIp = 0;
                    tcb->SeqNum = 1000;
                    tcb->AckNum = 0;
                    tcb->WindowSize = 8192;
                    tcb->RtoTick = 0;
                    tcb->RxHead = 0;
                    tcb->RxTail = 0;
                    tcb->RxCount = 0;
                    return i;
                }
            }
            return -1;
        }

        public static bool Bind(int sockId, uint ip, ushort port)
        {
            if (sockId < 0 || sockId >= MaxSockets) return false;
            TcpControlBlock* tcb = GetTcb(sockId);
            if (!tcb->InUse) return false;
            tcb->LocalIp = ip != 0 ? ip : ArpTable.LocalIp;
            tcb->LocalPort = port != 0 ? port : AllocateEphemeralPort();
            return true;
        }

        public static bool Listen(int sockId, uint backlog)
        {
            if (sockId < 0 || sockId >= MaxSockets) return false;
            TcpControlBlock* tcb = GetTcb(sockId);
            if (!tcb->InUse) return false;
            if (tcb->LocalPort == 0) tcb->LocalPort = AllocateEphemeralPort();
            tcb->State = TcpState.Listen;
            return true;
        }

        public static bool Connect(int sockId, uint ip, ushort port)
        {
            if (sockId < 0 || sockId >= MaxSockets) return false;
            TcpControlBlock* tcb = GetTcb(sockId);
            if (!tcb->InUse) return false;
            if (tcb->LocalPort == 0) tcb->LocalPort = AllocateEphemeralPort();
            tcb->LocalIp = ArpTable.LocalIp;
            tcb->RemoteIp = ip;
            tcb->RemotePort = port;
            tcb->SeqNum = 1000;
            tcb->AckNum = 0;
            tcb->State = TcpState.SynSent;

            SendSegment(tcb, TcpFlags.Syn, null, 0);
            return true;
        }

        public static uint Send(int sockId, byte* data, uint len)
        {
            if (sockId < 0 || sockId >= MaxSockets || data == null || len == 0) return 0;
            TcpControlBlock* tcb = GetTcb(sockId);
            if (!tcb->InUse || tcb->State != TcpState.Established) return 0;

            uint sent = 0;
            while (sent < len)
            {
                uint chunk = len - sent;
                if (chunk > 1400) chunk = 1400;

                SendSegment(tcb, (byte)(TcpFlags.Ack | TcpFlags.Psh), data + sent, (ushort)chunk);
                tcb->SeqNum += chunk;
                sent += chunk;
            }
            return sent;
        }

        public static uint Recv(int sockId, byte* outBuf, uint maxLen)
        {
            if (sockId < 0 || sockId >= MaxSockets || outBuf == null || maxLen == 0) return 0;
            TcpControlBlock* tcb = GetTcb(sockId);
            if (!tcb->InUse || tcb->RxCount == 0) return 0;

            uint toCopy = (uint)tcb->RxCount < maxLen ? (uint)tcb->RxCount : maxLen;
            for (uint i = 0; i < toCopy; i++)
            {
                outBuf[i] = tcb->RxBuffer[tcb->RxTail];
                tcb->RxTail = (tcb->RxTail + 1) % 4096;
            }
            tcb->RxCount -= (int)toCopy;
            return toCopy;
        }

        public static void Close(int sockId)
        {
            if (sockId < 0 || sockId >= MaxSockets) return;
            TcpControlBlock* tcb = GetTcb(sockId);
            if (!tcb->InUse) return;
            if (tcb->State == TcpState.Established)
            {
                SendSegment(tcb, (byte)(TcpFlags.Fin | TcpFlags.Ack), null, 0);
                tcb->SeqNum++;
                tcb->State = TcpState.FinWait1;
            }
            else
            {
                tcb->State = TcpState.Closed;
                tcb->InUse = false;
            }
        }

        private static void SendSegment(TcpControlBlock* tcb, byte flags, byte* payload, ushort payloadLen)
        {
            ushort tcpLen = (ushort)(20 + payloadLen);
            byte* tcpBuf = stackalloc byte[tcpLen];

            *(ushort*)(tcpBuf + 0) = BinaryPrimitives.ReverseEndianness(tcb->LocalPort);
            *(ushort*)(tcpBuf + 2) = BinaryPrimitives.ReverseEndianness(tcb->RemotePort);
            *(uint*)(tcpBuf + 4) = BinaryPrimitives.ReverseEndianness(tcb->SeqNum);
            *(uint*)(tcpBuf + 8) = BinaryPrimitives.ReverseEndianness(tcb->AckNum);
            tcpBuf[12] = 0x50; // Data offset 5 (20 bytes), reserved 0
            tcpBuf[13] = flags;
            *(ushort*)(tcpBuf + 14) = BinaryPrimitives.ReverseEndianness((ushort)8192); // Window size
            *(ushort*)(tcpBuf + 16) = 0; // Checksum placeholder
            *(ushort*)(tcpBuf + 18) = 0; // Urgent pointer

            if (payload != null && payloadLen > 0)
            {
                for (ushort i = 0; i < payloadLen; i++)
                {
                    tcpBuf[20 + i] = payload[i];
                }
            }

            // TCP Checksum with IPv4 Pseudo-Header
            uint sum = 0;
            sum += (tcb->LocalIp & 0xFFFF);
            sum += (tcb->LocalIp >> 16);
            sum += (tcb->RemoteIp & 0xFFFF);
            sum += (tcb->RemoteIp >> 16);
            sum += (uint)BinaryPrimitives.ReverseEndianness((ushort)6); // Protocol TCP
            sum += (uint)BinaryPrimitives.ReverseEndianness(tcpLen);

            ushort* words = (ushort*)tcpBuf;
            int wordCount = tcpLen / 2;
            for (int w = 0; w < wordCount; w++) sum += words[w];
            if ((tcpLen & 1) != 0) sum += tcpBuf[tcpLen - 1];

            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            *(ushort*)(tcpBuf + 16) = (ushort)~sum;

            Ipv4.SendIpv4Packet(tcb->RemoteIp, Ipv4.ProtocolTcp, tcpBuf, tcpLen);
        }

        public static void ProcessPacket(uint srcIp, uint dstIp, byte* tcpPacket, uint length)
        {
            if (length < 20) return;

            ushort srcPort = BinaryPrimitives.ReverseEndianness(*(ushort*)(tcpPacket + 0));
            ushort dstPort = BinaryPrimitives.ReverseEndianness(*(ushort*)(tcpPacket + 2));
            uint seqNum = BinaryPrimitives.ReverseEndianness(*(uint*)(tcpPacket + 4));
            uint ackNum = BinaryPrimitives.ReverseEndianness(*(uint*)(tcpPacket + 8));
            byte offsetByte = tcpPacket[12];
            int dataOffset = (offsetByte >> 4) * 4;
            byte flags = tcpPacket[13];

            if (dataOffset < 20 || (uint)dataOffset > length) return;
            uint payloadLen = length - (uint)dataOffset;
            byte* payload = tcpPacket + dataOffset;

            for (int i = 0; i < MaxSockets; i++)
            {
                TcpControlBlock* tcb = GetTcb(i);
                if (!tcb->InUse) continue;

                // Match listening socket or established connection
                if (tcb->State == TcpState.Listen && tcb->LocalPort == dstPort)
                {
                    if ((flags & TcpFlags.Syn) != 0)
                    {
                        tcb->RemoteIp = srcIp;
                        tcb->RemotePort = srcPort;
                        tcb->AckNum = seqNum + 1;
                        tcb->SeqNum = 2000;
                        tcb->State = TcpState.SynReceived;

                        // Send SYN-ACK
                        SendSegment(tcb, (byte)(TcpFlags.Syn | TcpFlags.Ack), null, 0);
                        tcb->SeqNum++;
                    }
                    return;
                }

                if (tcb->LocalPort == dstPort && tcb->RemotePort == srcPort && tcb->RemoteIp == srcIp)
                {
                    if (tcb->State == TcpState.SynSent && (flags & (TcpFlags.Syn | TcpFlags.Ack)) == (TcpFlags.Syn | TcpFlags.Ack))
                    {
                        tcb->AckNum = seqNum + 1;
                        tcb->SeqNum = ackNum;
                        tcb->State = TcpState.Established;

                        // Send ACK
                        SendSegment(tcb, TcpFlags.Ack, null, 0);
                        return;
                    }

                    if (tcb->State == TcpState.SynReceived && (flags & TcpFlags.Ack) != 0)
                    {
                        tcb->State = TcpState.Established;
                        return;
                    }

                    if (tcb->State == TcpState.Established)
                    {
                        if (payloadLen > 0)
                        {
                            for (uint b = 0; b < payloadLen && tcb->RxCount < 4096; b++)
                            {
                                tcb->RxBuffer[tcb->RxHead] = payload[b];
                                tcb->RxHead = (tcb->RxHead + 1) % 4096;
                                tcb->RxCount++;
                            }
                            tcb->AckNum = seqNum + payloadLen;
                            SendSegment(tcb, TcpFlags.Ack, null, 0);
                        }

                        if ((flags & TcpFlags.Fin) != 0)
                        {
                            tcb->AckNum = seqNum + 1;
                            SendSegment(tcb, (byte)(TcpFlags.Fin | TcpFlags.Ack), null, 0);
                            tcb->SeqNum++;
                            tcb->State = TcpState.TimeWait;
                        }
                        return;
                    }

                    if (tcb->State == TcpState.FinWait1 && (flags & TcpFlags.Ack) != 0)
                    {
                        tcb->State = TcpState.TimeWait;
                        tcb->InUse = false;
                        return;
                    }
                }
            }
        }
    }
}
