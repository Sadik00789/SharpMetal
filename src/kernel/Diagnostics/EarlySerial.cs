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

        public static void WriteChar(char c)
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

        public static void Write(string s)
        {
            if (!s_isSupported) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n') WriteChar('\r');
                WriteChar(c);
            }
        }

        public static void WriteLine(string s)
        {
            if (!s_isSupported) return;
            Write(s);
            WriteChar('\r');
            WriteChar('\n');
        }

        public static void WriteLine()
        {
            if (!s_isSupported) return;
            WriteChar('\r');
            WriteChar('\n');
        }

        public static void WriteHex(ulong value)
        {
            if (!s_isSupported) return;
            Write("0x");
            for (int i = 60; i >= 0; i -= 4)
            {
                byte nibble = (byte)((value >> i) & 0x0F);
                char c = (char)(nibble < 10 ? ('0' + nibble) : ('A' + (nibble - 10)));
                WriteChar(c);
            }
        }

        public static void WriteDec(long value)
        {
            if (!s_isSupported) return;
            if (value == 0) { WriteChar('0'); return; }
            if (value < 0) { WriteChar('-'); value = -value; }

            long divisor = 1;
            while (value / divisor >= 10) divisor *= 10;
            while (divisor > 0)
            {
                long digit = value / divisor;
                WriteChar((char)('0' + digit));
                value %= divisor;
                divisor /= 10;
            }
        }
    }
}
