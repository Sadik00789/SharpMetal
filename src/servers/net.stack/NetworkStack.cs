using System;
using Userland.Runtime.ZeroAlloc.Interop;

namespace NetStack
{
    public static unsafe class NetworkStack
    {
        public static delegate*<byte*, uint, void> PacketSender = null;
        public static byte Mac0 = 0x52;
        public static byte Mac1 = 0x54;
        public static byte Mac2 = 0x00;
        public static byte Mac3 = 0x12;
        public static byte Mac4 = 0x34;
        public static byte Mac5 = 0x56;

        public static void Initialize(byte* mac, delegate*<byte*, uint, void> sender)
        {
            PacketSender = sender;
            if (mac != null)
            {
                Mac0 = mac[0];
                Mac1 = mac[1];
                Mac2 = mac[2];
                Mac3 = mac[3];
                Mac4 = mac[4];
                Mac5 = mac[5];
            }
            byte* m = stackalloc byte[6];
            GetLocalMac(m);
            ArpTable.Initialize(m);
        }

        public static void GetLocalMac(byte* outMac)
        {
            outMac[0] = Mac0;
            outMac[1] = Mac1;
            outMac[2] = Mac2;
            outMac[3] = Mac3;
            outMac[4] = Mac4;
            outMac[5] = Mac5;
        }

        public static void SendFrame(byte* frame, uint length)
        {
            if (PacketSender != null)
            {
                PacketSender(frame, length);
            }
        }
    }
}
