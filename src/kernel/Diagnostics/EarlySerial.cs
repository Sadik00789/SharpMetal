using Kernel.Arch.x86_64.Hardware;

namespace Kernel.Diagnostics
{
    public static class EarlySerial
    {
        public const ushort Com1Base = 0x3F8;
        private static bool s_isSupported = false;

        public static void Initialize()
        {
            // Probe physical UART presence using Scratch Register (offset + 7)
            PortIo.Out8(Com1Base + 7, 0xA5);
            if (PortIo.In8(Com1Base + 7) != 0xA5)
            {
                s_isSupported = false;
                return;
            }

            PortIo.Out8(Com1Base + 7, 0x5A);
            if (PortIo.In8(Com1Base + 7) != 0x5A)
            {
                s_isSupported = false;
                return;
            }

            s_isSupported = true;

            PortIo.Out8(Com1Base + 1, 0x00); // Disable interrupts
            PortIo.Out8(Com1Base + 3, 0x80); // Enable DLAB
            PortIo.Out8(Com1Base + 0, 0x01); // 115200 baud
            PortIo.Out8(Com1Base + 1, 0x00);
            PortIo.Out8(Com1Base + 3, 0x03); // 8N1
            PortIo.Out8(Com1Base + 2, 0xC7); // Enable FIFO
            PortIo.Out8(Com1Base + 4, 0x0B); // RTS/DSR set
        }

        public static bool IsTransmitEmpty()
        {
            if (!s_isSupported) return false;
            return (PortIo.In8(Com1Base + 5) & 0x20) != 0;
        }

        private static Concurrency.SpinLockWithIrqSave s_serialLock;
        private static volatile int s_lockOwner = -1;
        private static int s_recursionDepth = 0;

        public static ulong AcquireLock()
        {
            ulong rflags = Cpu.ReadRflags();
            Cpu.DisableInterrupts();

            int coreId = LocalApic.IsInitialized ? CpuTopology.GetCurrentCoreIndex() : 0;
            if (s_lockOwner == coreId)
            {
                s_recursionDepth++;
                return rflags;
            }

            s_serialLock.Lock.Acquire();
            s_lockOwner = coreId;
            s_recursionDepth = 1;
            return rflags;
        }

        public static void ReleaseLock(ulong rflags)
        {
            int coreId = LocalApic.IsInitialized ? CpuTopology.GetCurrentCoreIndex() : 0;
            if (s_lockOwner == coreId)
            {
                s_recursionDepth--;
                if (s_recursionDepth == 0)
                {
                    s_lockOwner = -1;
                    s_serialLock.Lock.Release();
                }
            }

            Cpu.RestoreRflags(rflags);
        }

        public static void ForceResetLock()
        {
            s_serialLock.Lock.Serving = s_serialLock.Lock.NextTicket;
            s_lockOwner = -1;
            s_recursionDepth = 0;
        }

        public static void WriteCharInternal(char c)
        {
            if (!s_isSupported) return;

            int timeout = 1000;
            while (!IsTransmitEmpty() && timeout > 0)
            {
                PortIo.IoWait();
                timeout--;
            }

            if (timeout > 0)
            {
                PortIo.Out8(Com1Base, (byte)c);
            }
        }

        public static void WriteChar(char c)
        {
            if (!s_isSupported) return;
            ulong rflags = AcquireLock();
            try
            {
                WriteCharInternal(c);
            }
            finally
            {
                ReleaseLock(rflags);
            }
        }

        public static void WriteInternal(string s)
        {
            if (!s_isSupported || s == null) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n') WriteCharInternal('\r');
                WriteCharInternal(c);
            }
        }

        public static void Write(string s)
        {
            if (!s_isSupported || s == null) return;
            ulong rflags = AcquireLock();
            try
            {
                WriteInternal(s);
            }
            finally
            {
                ReleaseLock(rflags);
            }
        }

        public static unsafe void WriteBytes(byte* msg)
        {
            if (!s_isSupported || msg == null) return;
            ulong rflags = AcquireLock();
            try
            {
                int limit = 2048;
                while (*msg != 0 && limit-- > 0)
                {
                    char c = (char)(*msg++);
                    if (c == '\n') WriteCharInternal('\r');
                    WriteCharInternal(c);
                }
            }
            finally
            {
                ReleaseLock(rflags);
            }
        }

        public static void WriteLine(string s)
        {
            if (!s_isSupported) return;
            ulong rflags = AcquireLock();
            try
            {
                WriteInternal(s);
                WriteCharInternal('\r');
                WriteCharInternal('\n');
            }
            finally
            {
                ReleaseLock(rflags);
            }
        }

        public static void WriteLine()
        {
            if (!s_isSupported) return;
            ulong rflags = AcquireLock();
            try
            {
                WriteCharInternal('\r');
                WriteCharInternal('\n');
            }
            finally
            {
                ReleaseLock(rflags);
            }
        }

        public static void WriteHexInternal(ulong value)
        {
            if (!s_isSupported) return;
            WriteInternal("0x");
            for (int i = 60; i >= 0; i -= 4)
            {
                byte nibble = (byte)((value >> i) & 0x0F);
                char c = (char)(nibble < 10 ? ('0' + nibble) : ('A' + (nibble - 10)));
                WriteCharInternal(c);
            }
        }

        public static void WriteHex(ulong value)
        {
            if (!s_isSupported) return;
            ulong rflags = AcquireLock();
            try
            {
                WriteHexInternal(value);
            }
            finally
            {
                ReleaseLock(rflags);
            }
        }

        public static void WriteDecInternal(long value)
        {
            if (!s_isSupported) return;
            if (value == 0) { WriteCharInternal('0'); return; }
            if (value < 0) { WriteCharInternal('-'); value = -value; }

            long divisor = 1;
            while (value / divisor >= 10) divisor *= 10;
            while (divisor > 0)
            {
                long digit = value / divisor;
                WriteCharInternal((char)('0' + digit));
                value %= divisor;
                divisor /= 10;
            }
        }

        public static void WriteDec(long value)
        {
            if (!s_isSupported) return;
            ulong rflags = AcquireLock();
            try
            {
                WriteDecInternal(value);
            }
            finally
            {
                ReleaseLock(rflags);
            }
        }
    }
}
