using System;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Shell
{
    public static unsafe class TerminalHistory
    {
        public const int Capacity = 32;
        public const int MaxCommandLength = 64;

        private static byte* s_storage = null;
        private static int s_count = 0;
        private static int s_head = 0;

        public static void Initialize()
        {
            if (s_storage == null)
            {
                SyscallWrappers.AllocDma(4096, 0x3C000000UL);
                s_storage = (byte*)0x3C000000UL;
            }
            s_count = 0;
            s_head = 0;
            for (int i = 0; i < Capacity * MaxCommandLength; i++)
            {
                s_storage[i] = 0;
            }
        }

        public static void Add(string cmd)
        {
            if (cmd == null || cmd.Length == 0 || s_storage == null) return;

            byte* slot = s_storage + (s_head * MaxCommandLength);
            int len = 0;
            for (; len < cmd.Length && len < MaxCommandLength - 1; len++)
            {
                slot[len] = (byte)cmd[len];
            }
            slot[len] = 0;

            s_head = (s_head + 1) % Capacity;
            if (s_count < Capacity)
            {
                s_count++;
            }
        }

        public static void Add(byte* cmd, int length)
        {
            if (cmd == null || length <= 0 || s_storage == null) return;

            byte* slot = s_storage + (s_head * MaxCommandLength);
            int len = 0;
            for (; len < length && len < MaxCommandLength - 1; len++)
            {
                slot[len] = cmd[len];
            }
            slot[len] = 0;

            s_head = (s_head + 1) % Capacity;
            if (s_count < Capacity)
            {
                s_count++;
            }
        }

        public static int Count => s_count;
        public static int Head => s_head;

        public static bool TryGetEntry(int indexFromLatest, byte* dest, out int length)
        {
            length = 0;
            if (s_count == 0 || indexFromLatest < 0 || indexFromLatest >= s_count || s_storage == null || dest == null)
            {
                return false;
            }

            int slotIdx = (s_head - 1 - indexFromLatest + (Capacity * 10)) % Capacity;
            byte* slot = s_storage + (slotIdx * MaxCommandLength);

            int len = 0;
            while (len < MaxCommandLength && slot[len] != 0)
            {
                dest[len] = slot[len];
                len++;
            }
            dest[len] = 0;
            length = len;
            return true;
        }
    }
}
