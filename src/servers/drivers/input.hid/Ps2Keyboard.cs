using System;
using System.Runtime.InteropServices;
using Userland.Runtime.ZeroAlloc.Interop;

namespace InputHid
{
    public static class Ps2Keyboard
    {
        [DllImport("*")]
        public static extern byte PortIn8(ushort port);

        [DllImport("*")]
        public static extern void PortOut8(ushort port, byte val);

        private const ushort DataPort = 0x60;
        private const ushort StatusPort = 0x64;

        private static bool s_shiftDown = false;
        private static bool s_capsLock = false;
        private static bool s_extended = false;
        private static byte s_pending0 = 0;
        private static byte s_pending1 = 0;

        private static void WaitInputEmpty()
        {
            int timeout = 50000;
            while ((PortIn8(StatusPort) & 2) != 0 && timeout-- > 0)
            {
            }
        }

        private static void WaitOutputFull()
        {
            int timeout = 50000;
            while ((PortIn8(StatusPort) & 1) == 0 && timeout-- > 0)
            {
            }
        }

        public static void Initialize()
        {
            s_shiftDown = false;
            s_capsLock = false;
            s_extended = false;
            s_pending0 = 0;
            s_pending1 = 0;

            // 1. Drain residual bytes in PS/2 buffer
            int maxFlush = 128;
            while ((PortIn8(StatusPort) & 1) != 0 && maxFlush-- > 0)
            {
                PortIn8(DataPort);
            }

            // 2. Enable first PS/2 keyboard port (Command 0xAE to StatusPort)
            WaitInputEmpty();
            PortOut8(StatusPort, 0xAE);

            // 3. Read Controller Command Byte (Command 0x20)
            WaitInputEmpty();
            PortOut8(StatusPort, 0x20);
            WaitOutputFull();
            byte config = PortIn8(DataPort);

            // Bit 0: First port interrupt (1 = enabled)
            // Bit 4: First port clock (0 = enabled)
            // Bit 6: Translation (1 = enabled, translates Set 2 to Set 1)
            config |= 0x01;
            config &= 0xEF;
            config |= 0x40;

            WaitInputEmpty();
            PortOut8(StatusPort, 0x60); // Command 0x60: Write Controller Command Byte
            WaitInputEmpty();
            PortOut8(DataPort, config);

            // 4. Enable Keyboard Scanning (Command 0xF4 to DataPort)
            WaitInputEmpty();
            PortOut8(DataPort, 0xF4);

            // Wait for ACK (0xFA)
            int ackTimeout = 50000;
            while (ackTimeout-- > 0)
            {
                if ((PortIn8(StatusPort) & 1) != 0)
                {
                    byte resp = PortIn8(DataPort);
                    if (resp == 0xFA) break;
                }
            }

            // Flush any remaining response bytes
            maxFlush = 32;
            while ((PortIn8(StatusPort) & 1) != 0 && maxFlush-- > 0)
            {
                PortIn8(DataPort);
            }

            // Serial Token 4
            SyscallWrappers.Log("[INPUT] PS/2 keyboard controller online.\n");
        }

        public static uint ReadKey()
        {
            if (s_pending0 != 0)
            {
                uint queued = s_pending0;
                s_pending0 = s_pending1;
                s_pending1 = 0;
                return queued;
            }

            // Check if output buffer full
            byte status = PortIn8(StatusPort);
            if ((status & 1) == 0)
            {
                return 0; // No key available
            }

            byte sc = PortIn8(DataPort);

            // Filter out auxiliary / mouse data (bit 5 of status port is set)
            if ((status & 0x20) != 0)
            {
                return 0;
            }

            // Filter out ACK / Resend / Echo / Error bytes
            if (sc == 0xFA || sc == 0xFE || sc == 0xEE || sc == 0xFF || sc == 0x00)
            {
                return 0;
            }

            // Extended scancode prefix
            if (sc == 0xE0)
            {
                s_extended = true;
                return 0;
            }

            if (s_extended)
            {
                s_extended = false;
                if ((sc & 0x80) != 0) return 0; // Key release

                // Up Arrow: 0xE0 0x48 -> \x1b[A
                if (sc == 0x48)
                {
                    s_pending0 = (byte)'[';
                    s_pending1 = (byte)'A';
                    return 0x1B;
                }
                // Down Arrow: 0xE0 0x50 -> \x1b[B
                if (sc == 0x50)
                {
                    s_pending0 = (byte)'[';
                    s_pending1 = (byte)'B';
                    return 0x1B;
                }
                // Left Arrow: 0xE0 0x4B -> \x1b[D
                if (sc == 0x4B)
                {
                    s_pending0 = (byte)'[';
                    s_pending1 = (byte)'D';
                    return 0x1B;
                }
                // Right Arrow: 0xE0 0x4D -> \x1b[C
                if (sc == 0x4D)
                {
                    s_pending0 = (byte)'[';
                    s_pending1 = (byte)'C';
                    return 0x1B;
                }
                return 0;
            }

            // Handle Shift press / release
            if (sc == 0x2A || sc == 0x36) // Left / Right Shift down
            {
                s_shiftDown = true;
                return 0;
            }
            if (sc == 0xAA || sc == 0xB6) // Left / Right Shift up
            {
                s_shiftDown = false;
                return 0;
            }

            // Handle CapsLock toggle
            if (sc == 0x3A)
            {
                s_capsLock = !s_capsLock;
                return 0;
            }

            // Ignore break codes (key releases: bit 7 set)
            if ((sc & 0x80) != 0)
            {
                return 0;
            }

            return TranslateScancode(sc, s_shiftDown ^ s_capsLock, s_shiftDown);
        }

        private static uint TranslateScancode(byte sc, bool caps, bool shift)
        {
            switch (sc)
            {
                case 0x01: return 27;   // Esc
                case 0x0E: return '\b'; // Backspace
                case 0x0F: return '\t'; // Tab
                case 0x1C: return '\n'; // Enter
                case 0x39: return ' ';  // Space

                // Digits
                case 0x02: return shift ? '!' : '1';
                case 0x03: return shift ? '@' : '2';
                case 0x04: return shift ? '#' : '3';
                case 0x05: return shift ? '$' : '4';
                case 0x06: return shift ? '%' : '5';
                case 0x07: return shift ? '^' : '6';
                case 0x08: return shift ? '&' : '7';
                case 0x09: return shift ? '*' : '8';
                case 0x0A: return shift ? '(' : '9';
                case 0x0B: return shift ? ')' : '0';

                // Top row
                case 0x10: return caps ? 'Q' : 'q';
                case 0x11: return caps ? 'W' : 'w';
                case 0x12: return caps ? 'E' : 'e';
                case 0x13: return caps ? 'R' : 'r';
                case 0x14: return caps ? 'T' : 't';
                case 0x15: return caps ? 'Y' : 'y';
                case 0x16: return caps ? 'U' : 'u';
                case 0x17: return caps ? 'I' : 'i';
                case 0x18: return caps ? 'O' : 'o';
                case 0x19: return caps ? 'P' : 'p';
                case 0x1A: return shift ? '{' : '[';
                case 0x1B: return shift ? '}' : ']';

                // Home row
                case 0x1E: return caps ? 'A' : 'a';
                case 0x1F: return caps ? 'S' : 's';
                case 0x20: return caps ? 'D' : 'd';
                case 0x21: return caps ? 'F' : 'f';
                case 0x22: return caps ? 'G' : 'g';
                case 0x23: return caps ? 'H' : 'h';
                case 0x24: return caps ? 'J' : 'j';
                case 0x25: return caps ? 'K' : 'k';
                case 0x26: return caps ? 'L' : 'l';
                case 0x27: return shift ? ':' : ';';
                case 0x28: return shift ? '"' : '\'';

                // Bottom row
                case 0x2C: return caps ? 'Z' : 'z';
                case 0x2D: return caps ? 'X' : 'x';
                case 0x2E: return caps ? 'C' : 'c';
                case 0x2F: return caps ? 'V' : 'v';
                case 0x30: return caps ? 'B' : 'b';
                case 0x31: return caps ? 'N' : 'n';
                case 0x32: return caps ? 'M' : 'm';
                case 0x33: return shift ? '<' : ',';
                case 0x34: return shift ? '>' : '.';
                case 0x35: return shift ? '?' : '/';

                // Symbols
                case 0x0C: return shift ? '_' : '-';
                case 0x0D: return shift ? '+' : '=';
                case 0x29: return shift ? '~' : '`';
                case 0x2B: return shift ? '|' : '\\';

                default:
                    return 0;
            }
        }
    }
}
