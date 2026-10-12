using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Boot;
using Kernel.Concurrency;
using Kernel.Memory.Virtual;

namespace Kernel.Diagnostics
{
    public static unsafe class DisplayConsole
    {
        [DllImport("*")]
        public static extern byte* GetKernelFontGlyphs();

        public static volatile bool Muted = false;

        private static bool s_initialized = false;
        private static uint* s_fb = null;
        private static uint s_width = 0;
        private static uint s_height = 0;
        private static uint s_pitch = 0;
        private static int s_cols = 0;
        private static int s_rows = 0;
        private static int s_cursorX = 0;
        private static int s_cursorY = 0;
        private static byte* s_fontData = null;

        private const uint BgColor = 0xFF1E1E2E;
        private const uint FgColor = 0xFFCDD6F4;

        private static SpinLockWithIrqSave s_lock;

        public static void EnsureInitialized()
        {
            if (s_initialized) return;
            if (KernelHigh.GopPhysBase == 0 || KernelHigh.GopFbSize == 0) return;

            s_fb = (uint*)Hhdm.PhysicalToVirtual(KernelHigh.GopPhysBase);
            s_width = (uint)KernelHigh.GopWidth;
            s_height = (uint)KernelHigh.GopHeight;
            s_pitch = (uint)(KernelHigh.GopPixelsPerScanLine > 0 ? KernelHigh.GopPixelsPerScanLine : s_width);
            s_cols = (int)(s_width / 8);
            s_rows = (int)(s_height / 16);
            s_fontData = GetKernelFontGlyphs();
            s_initialized = true;
        }

        public static void ClearFullDisplay(uint color = BgColor)
        {
            if (KernelHigh.GopPhysBase == 0 || KernelHigh.GopFbSize == 0) return;
            ulong rflags = s_lock.Acquire();
            try
            {
                EnsureInitialized();
                if (s_fb == null) return;
                ulong totalPixels = (ulong)s_pitch * s_height;
                if (totalPixels == 0) totalPixels = KernelHigh.GopFbSize / 4UL;
                for (ulong i = 0; i < totalPixels; i++)
                {
                    s_fb[i] = color;
                }
                s_cursorX = 0;
                s_cursorY = 0;
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static void WriteBytes(byte* msg)
        {
            if (msg == null || Muted) return;
            if (KernelHigh.GopPhysBase == 0) return;

            ulong rflags = s_lock.Acquire();
            try
            {
                if (Muted) return;
                EnsureInitialized();
                if (s_fb == null || s_fontData == null) return;

                int limit = 4096;
                while (*msg != 0 && limit-- > 0)
                {
                    byte b = *msg++;
                    if (b == 0x1B) // ESC
                    {
                        if (*msg == (byte)'[')
                        {
                            msg++;
                            while (*msg != 0 && !((*msg >= (byte)'A' && *msg <= (byte)'Z') || (*msg >= (byte)'a' && *msg <= (byte)'z') || *msg == (byte)'~'))
                            {
                                msg++;
                            }
                            if (*msg != 0) msg++;
                        }
                        continue;
                    }
                    WriteCharInternal((char)b);
                }
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static void WriteChar(char c)
        {
            if (Muted || KernelHigh.GopPhysBase == 0) return;

            ulong rflags = s_lock.Acquire();
            try
            {
                if (Muted) return;
                EnsureInitialized();
                if (s_fb == null || s_fontData == null) return;
                WriteCharInternal(c);
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static void Write(string s)
        {
            if (s == null || Muted) return;
            if (KernelHigh.GopPhysBase == 0) return;

            ulong rflags = s_lock.Acquire();
            try
            {
                if (Muted) return;
                EnsureInitialized();
                if (s_fb == null || s_fontData == null) return;

                for (int i = 0; i < s.Length; i++)
                {
                    WriteCharInternal(s[i]);
                }
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static void WriteLine(string s = "")
        {
            if (Muted || KernelHigh.GopPhysBase == 0) return;

            ulong rflags = s_lock.Acquire();
            try
            {
                if (Muted) return;
                EnsureInitialized();
                if (s_fb == null || s_fontData == null) return;

                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        WriteCharInternal(s[i]);
                    }
                }
                WriteCharInternal('\n');
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static void WriteHex(ulong value)
        {
            if (Muted || KernelHigh.GopPhysBase == 0) return;

            ulong rflags = s_lock.Acquire();
            try
            {
                if (Muted) return;
                EnsureInitialized();
                if (s_fb == null || s_fontData == null) return;

                WriteCharInternal('0');
                WriteCharInternal('x');
                for (int i = 60; i >= 0; i -= 4)
                {
                    byte nibble = (byte)((value >> i) & 0x0F);
                    char c = (char)(nibble < 10 ? ('0' + nibble) : ('A' + (nibble - 10)));
                    WriteCharInternal(c);
                }
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static void WriteDec(long value)
        {
            if (Muted || KernelHigh.GopPhysBase == 0) return;

            ulong rflags = s_lock.Acquire();
            try
            {
                if (Muted) return;
                EnsureInitialized();
                if (s_fb == null || s_fontData == null) return;

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
            finally
            {
                s_lock.Release(rflags);
            }
        }

        private static void WriteCharInternal(char c)
        {
            if (s_fb == null || s_fontData == null) return;

            if (c == '\r')
            {
                s_cursorX = 0;
                return;
            }
            if (c == '\n')
            {
                s_cursorX = 0;
                s_cursorY++;
                if (s_cursorY >= s_rows) Scroll();
                return;
            }
            if (c == '\b')
            {
                if (s_cursorX > 0)
                {
                    s_cursorX--;
                    DrawGlyph(s_cursorX, s_cursorY, ' ');
                }
                return;
            }
            if (c == '\t')
            {
                int nextTab = (s_cursorX + 4) & ~3;
                while (s_cursorX < nextTab && s_cursorX < s_cols)
                {
                    DrawGlyph(s_cursorX, s_cursorY, ' ');
                    s_cursorX++;
                }
                if (s_cursorX >= s_cols)
                {
                    s_cursorX = 0;
                    s_cursorY++;
                    if (s_cursorY >= s_rows) Scroll();
                }
                return;
            }
            if (c < 32 || c > 126)
            {
                return;
            }

            DrawGlyph(s_cursorX, s_cursorY, c);
            s_cursorX++;
            if (s_cursorX >= s_cols)
            {
                s_cursorX = 0;
                s_cursorY++;
                if (s_cursorY >= s_rows) Scroll();
            }
        }

        private static void DrawGlyph(int col, int row, char c)
        {
            if (col < 0 || col >= s_cols || row < 0 || row >= s_rows) return;
            byte* glyph = s_fontData + (((int)(byte)c) * 16);
            int pxBase = col * 8;
            int pyBase = row * 16;

            for (int y = 0; y < 16; y++)
            {
                byte rowBits = glyph[y];
                uint* line = s_fb + (((ulong)(pyBase + y) * (ulong)s_pitch) + (ulong)pxBase);

                for (int x = 0; x < 8; x++)
                {
                    bool bit = ((rowBits >> (7 - x)) & 1) != 0;
                    line[x] = bit ? FgColor : BgColor;
                }
            }
        }

        private static void Scroll()
        {
            if (s_rows <= 1) return;

            ulong pixelsToMove = (ulong)(s_rows - 1) * 16UL * (ulong)s_pitch;
            uint* dst = s_fb;
            uint* src = s_fb + (16 * s_pitch);

            ulong* d64 = (ulong*)dst;
            ulong* s64 = (ulong*)src;
            ulong words64 = pixelsToMove / 2UL;
            for (ulong i = 0; i < words64; i++)
            {
                d64[i] = s64[i];
            }

            uint* bottomLine = s_fb + ((ulong)(s_rows - 1) * 16UL * (ulong)s_pitch);
            ulong clearPixels = 16UL * (ulong)s_pitch;
            ulong clearWords = clearPixels / 2UL;
            ulong clearVal = ((ulong)BgColor << 32) | (ulong)BgColor;
            ulong* c64 = (ulong*)bottomLine;
            for (ulong i = 0; i < clearWords; i++)
            {
                c64[i] = clearVal;
            }

            s_cursorY = s_rows - 1;
        }
    }
}
