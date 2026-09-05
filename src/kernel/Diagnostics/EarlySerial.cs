using Kernel.Arch.x86_64.Hardware;

namespace Kernel.Diagnostics
{
    public static class EarlySerial
    {
        public const ushort Com1Base = 0x3F8;

        public static void Initialize()
        {
            PortIo.Out8(Com1Base + 1, 0x00); // Disable interrupts
            PortIo.Out8(Com1Base + 3, 0x80); // Enable DLAB (set baud rate divisor)
            PortIo.Out8(Com1Base + 0, 0x01); // Divisor 1 = 115200 baud (low byte)
            PortIo.Out8(Com1Base + 1, 0x00); // (high byte)
            PortIo.Out8(Com1Base + 3, 0x03); // 8 bits, no parity, one stop bit
            PortIo.Out8(Com1Base + 2, 0xC7); // Enable FIFO, clear TX/RX, 14-byte threshold
            PortIo.Out8(Com1Base + 4, 0x0B); // IRQs enabled, RTS/DSR set
        }

        public static bool IsTransmitEmpty()
        {
            return (PortIo.In8(Com1Base + 5) & 0x20) != 0;
        }

        public static void WriteChar(char c)
        {
            while (!IsTransmitEmpty())
            {
                PortIo.IoWait();
            }
            PortIo.Out8(Com1Base, (byte)c);
        }

        public static void Write(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n')
                {
                    WriteChar('\r');
                }
                WriteChar(c);
            }
        }

        public static void WriteLine(string s)
        {
            Write(s);
            WriteChar('\r');
            WriteChar('\n');
        }

        public static void WriteLine()
        {
            WriteChar('\r');
            WriteChar('\n');
        }

        public static void WriteHex(ulong value)
        {
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
            if (value == 0)
            {
                WriteChar('0');
                return;
            }

            if (value < 0)
            {
                WriteChar('-');
                value = -value;
            }

            long divisor = 1;
            while (value / divisor >= 10)
            {
                divisor *= 10;
            }

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
