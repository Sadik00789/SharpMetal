using System;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetStack
{
    public enum SocketType
    {
        None = 0,
        Tcp  = 1,
        Udp  = 2,
        Raw  = 3
    }

    public struct SocketEntry
    {
        public bool InUse;
        public SocketType Type;
        public int InnerId;
    }

    public unsafe struct SocketManagerStorage
    {
        public fixed byte Raw[16 * 16];
    }

    public static unsafe class SocketManager
    {
        public const int MaxFds = 16;
        private static SocketManagerStorage s_storage;

        public static SocketEntry* GetEntry(int index)
        {
            fixed (byte* p = s_storage.Raw)
            {
                return (SocketEntry*)(p + (index * 16));
            }
        }

        public static uint Socket(uint domain, uint type, uint protocol)
        {
            // type 1 = SOCK_STREAM (TCP), type 2 = SOCK_DGRAM (UDP)
            SocketType sockType = type == 1 ? SocketType.Tcp : (type == 2 ? SocketType.Udp : SocketType.None);
            if (sockType == SocketType.None) return 0;

            int innerId = sockType == SocketType.Udp ? UdpSocket.AllocateSocket() : TcpSocket.AllocateSocket();
            if (innerId < 0) return 0;

            for (int i = 0; i < MaxFds; i++)
            {
                SocketEntry* entry = GetEntry(i);
                if (!entry->InUse)
                {
                    entry->InUse = true;
                    entry->Type = sockType;
                    entry->InnerId = innerId;
                    return (uint)(i + 1); // 1-based FD
                }
            }

            if (sockType == SocketType.Udp) UdpSocket.Close(innerId);
            else if (sockType == SocketType.Tcp) TcpSocket.Close(innerId);
            return 0;
        }

        public static uint Bind(uint socketFd, uint ip, ushort port)
        {
            if (socketFd == 0 || socketFd > MaxFds) return 1;
            SocketEntry* entry = GetEntry((int)(socketFd - 1));
            if (!entry->InUse) return 1;
            if (entry->Type == SocketType.Udp)
            {
                return UdpSocket.Bind(entry->InnerId, ip, port) ? 0U : 1U;
            }
            if (entry->Type == SocketType.Tcp)
            {
                return TcpSocket.Bind(entry->InnerId, ip, port) ? 0U : 1U;
            }
            return 1;
        }

        public static uint Listen(uint socketFd, uint backlog)
        {
            if (socketFd == 0 || socketFd > MaxFds) return 1;
            SocketEntry* entry = GetEntry((int)(socketFd - 1));
            if (!entry->InUse || entry->Type != SocketType.Tcp) return 1;
            return TcpSocket.Listen(entry->InnerId, backlog) ? 0U : 1U;
        }

        public static uint Accept(uint socketFd, ulong outAddrPhys)
        {
            return 0;
        }

        public static uint Connect(uint socketFd, uint ip, ushort port)
        {
            if (socketFd == 0 || socketFd > MaxFds) return 1;
            SocketEntry* entry = GetEntry((int)(socketFd - 1));
            if (!entry->InUse || entry->Type != SocketType.Tcp) return 1;
            return TcpSocket.Connect(entry->InnerId, ip, port) ? 0U : 1U;
        }

        public static uint Send(uint socketFd, ulong bufPhys, uint len, uint flags)
        {
            if (socketFd == 0 || socketFd > MaxFds || bufPhys == 0 || len == 0) return 0;
            SocketEntry* entry = GetEntry((int)(socketFd - 1));
            if (!entry->InUse) return 0;

            uint sendLen = len > 4096U ? 4096U : len;
            if (entry->Type == SocketType.Udp && sendLen > 1472U)
            {
                sendLen = 1472U;
            }

            ulong pagePhys = bufPhys & ~0xFFFUL;
            ulong pageOff = bufPhys & 0xFFFUL;
            ulong mapSize = (pageOff + sendLen + 4095UL) & ~4095UL;
            ulong clientVirt = 0x27040000UL;
            SyscallWrappers.MapMmio(pagePhys, clientVirt, mapSize, writeCombining: false);
            byte* data = (byte*)(clientVirt + pageOff);

            if (entry->Type == SocketType.Udp)
            {
                return UdpSocket.SendTo(entry->InnerId, ArpTable.GatewayIp, 8080, data, (ushort)sendLen);
            }
            if (entry->Type == SocketType.Tcp)
            {
                return TcpSocket.Send(entry->InnerId, data, sendLen);
            }
            return 0;
        }

        public static uint Recv(uint socketFd, ulong bufPhys, uint maxLen, uint flags)
        {
            if (socketFd == 0 || socketFd > MaxFds || bufPhys == 0 || maxLen == 0) return 0;
            SocketEntry* entry = GetEntry((int)(socketFd - 1));
            if (!entry->InUse) return 0;

            uint recvMax = maxLen > 4096U ? 4096U : maxLen;
            ulong pagePhys = bufPhys & ~0xFFFUL;
            ulong pageOff = bufPhys & 0xFFFUL;
            ulong mapSize = (pageOff + recvMax + 4095UL) & ~4095UL;
            ulong clientVirt = 0x27050000UL;
            SyscallWrappers.MapMmio(pagePhys, clientVirt, mapSize, writeCombining: false);
            byte* outBuf = (byte*)(clientVirt + pageOff);

            if (entry->Type == SocketType.Udp)
            {
                return UdpSocket.RecvFrom(entry->InnerId, outBuf, recvMax);
            }
            if (entry->Type == SocketType.Tcp)
            {
                return TcpSocket.Recv(entry->InnerId, outBuf, recvMax);
            }
            return 0;
        }

        public static uint Close(uint socketFd)
        {
            if (socketFd == 0 || socketFd > MaxFds) return 1;
            SocketEntry* entry = GetEntry((int)(socketFd - 1));
            if (!entry->InUse) return 1;
            if (entry->Type == SocketType.Udp)
            {
                UdpSocket.Close(entry->InnerId);
            }
            else if (entry->Type == SocketType.Tcp)
            {
                TcpSocket.Close(entry->InnerId);
            }
            entry->InUse = false;
            return 0;
        }
    }
}
