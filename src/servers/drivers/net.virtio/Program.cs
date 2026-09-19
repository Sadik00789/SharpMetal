using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Ipc;
using Microkernel.Abstractions.Services;
using NetStack;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetVirtio
{
    public struct NetworkServiceImpl : INetworkService
    {
        public uint GetMacAddress(ulong outMacBufferPhys)
        {
            return VirtioNetDriver.GetMacAddress(outMacBufferPhys);
        }

        public uint SendPacket(ulong packetPhys, uint length)
        {
            return VirtioNetDriver.SendPacket(packetPhys, length);
        }

        public uint ReceivePacket(ulong packetPhys, uint maxLength)
        {
            return VirtioNetDriver.ReceivePacket(packetPhys, maxLength);
        }

        public uint Socket(uint domain, uint type, uint protocol)
        {
            return SocketManager.Socket(domain, type, protocol);
        }

        public uint Bind(uint socketFd, uint ip, ushort port)
        {
            return SocketManager.Bind(socketFd, ip, port);
        }

        public uint Listen(uint socketFd, uint backlog)
        {
            return SocketManager.Listen(socketFd, backlog);
        }

        public uint Accept(uint socketFd, ulong outAddrPhys)
        {
            return SocketManager.Accept(socketFd, outAddrPhys);
        }

        public uint Connect(uint socketFd, uint ip, ushort port)
        {
            return SocketManager.Connect(socketFd, ip, port);
        }

        public uint Send(uint socketFd, ulong bufPhys, uint len, uint flags)
        {
            return SocketManager.Send(socketFd, bufPhys, len, flags);
        }

        public uint Recv(uint socketFd, ulong bufPhys, uint maxLen, uint flags)
        {
            return SocketManager.Recv(socketFd, bufPhys, maxLen, flags);
        }

        public uint Close(uint socketFd)
        {
            return SocketManager.Close(socketFd);
        }
    }

    public static unsafe class Program
    {
        public static NetworkServiceImpl s_serviceImpl;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "VirtioNetMain")]
        public static void Main()
        {
            for (int i = 0; i < 8; i++)
            {
                SyscallWrappers.Yield();
            }

            // Initialize VirtIO Modern Network device and configure virtqueues
            VirtioNetDriver.Initialize();

            // Run Frontier 3 self-test (Internal loopback: ARP and ICMP Echo)
            FrontierTests.NetTest.Run();

            s_serviceImpl = new NetworkServiceImpl();

            const uint EndpointCptr = 12;
            const uint NotificationCptr = 14;

            while (true)
            {
                ulong msgType, d0, d1, d2, d3, badge;
                ulong status = SyscallWrappers.RecvAny(EndpointCptr, NotificationCptr, out msgType, out d0, out d1, out d2, out d3, out badge);
                if (status != 0)
                {
                    SyscallWrappers.Yield();
                    continue;
                }

                bool isNotification = (msgType == IpcMessageHeader.AsyncNotification && (d0 == 0x31 || (d0 & 0x31) != 0)) || badge == 0x31;
                if (isNotification)
                {
                    VirtioNetDriver.ProcessRxPackets();
                }
                else
                {
                    // RPC invocation on Slot 12
                    uint methodId = (uint)(msgType & 0xFFFFFFFFUL);
                    ulong result = 0;
                    switch (methodId)
                    {
                        case 1:
                            result = s_serviceImpl.GetMacAddress(d0);
                            break;
                        case 2:
                            result = s_serviceImpl.SendPacket(d0, (uint)d1);
                            break;
                        case 3:
                            result = s_serviceImpl.ReceivePacket(d0, (uint)d1);
                            break;
                        case 4:
                            result = s_serviceImpl.Socket((uint)d0, (uint)d1, (uint)d2);
                            break;
                        case 5:
                            result = s_serviceImpl.Bind((uint)d0, (uint)d1, (ushort)d2);
                            break;
                        case 6:
                            result = s_serviceImpl.Listen((uint)d0, (uint)d1);
                            break;
                        case 7:
                            result = s_serviceImpl.Accept((uint)d0, d1);
                            break;
                        case 8:
                            result = s_serviceImpl.Connect((uint)d0, (uint)d1, (ushort)d2);
                            break;
                        case 9:
                            result = s_serviceImpl.Send((uint)d0, d1, (uint)d2, (uint)d3);
                            break;
                        case 10:
                            result = s_serviceImpl.Recv((uint)d0, d1, (uint)d2, (uint)d3);
                            break;
                        case 11:
                            result = s_serviceImpl.Close((uint)d0);
                            break;
                        default:
                            result = 0xFFFFFFFFFFFFFFFFUL;
                            break;
                    }
                    SyscallWrappers.Reply(result, 0, 0, 0);

                    // Check for pending RX packets after servicing RPC
                    VirtioNetDriver.ProcessRxPackets();
                }
            }
        }
    }
}
