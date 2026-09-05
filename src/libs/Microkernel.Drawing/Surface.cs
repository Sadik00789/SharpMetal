using System;

namespace Microkernel.Drawing
{
    public unsafe struct Surface
    {
        public int Width;
        public int Height;
        public uint* Pixels;

        public Surface(uint* pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }

        public void Clear(uint color)
        {
            if (Pixels == null) return;
            int total = Width * Height;
            for (int i = 0; i < total; i++)
            {
                Pixels[i] = color;
            }
        }

        public void DrawRect(int x, int y, int w, int h, uint color)
        {
            if (Pixels == null) return;
            for (int row = y; row < y + h; row++)
            {
                if (row < 0 || row >= Height) continue;
                int rowOffset = row * Width;
                for (int col = x; col < x + w; col++)
                {
                    if (col >= 0 && col < Width)
                    {
                        Pixels[rowOffset + col] = color;
                    }
                }
            }
        }

        public void DrawChar(int x, int y, char c, uint fgColor, uint bgColor)
        {
            if (Pixels == null) return;
            byte* glyph = BitmapFont.GetGlyph(c);
            if (glyph == null) return;

            for (int row = 0; row < BitmapFont.GlyphHeight; row++)
            {
                int py = y + row;
                if (py < 0 || py >= Height) continue;

                byte bits = glyph[row];
                int rowOffset = py * Width;

                for (int col = 0; col < BitmapFont.GlyphWidth; col++)
                {
                    int px = x + col;
                    if (px < 0 || px >= Width) continue;

                    bool set = ((bits >> (7 - col)) & 1) != 0;
                    Pixels[rowOffset + px] = set ? fgColor : bgColor;
                }
            }
        }

        public void DrawString(int x, int y, string text, uint fgColor, uint bgColor)
        {
            if (text == null) return;
            int curX = x;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    y += BitmapFont.GlyphHeight;
                    curX = x;
                }
                else
                {
                    DrawChar(curX, y, c, fgColor, bgColor);
                    curX += BitmapFont.GlyphWidth;
                }
            }
        }

        public void ScrollUp(int lineCount, uint bgColor)
        {
            if (Pixels == null || lineCount <= 0) return;
            int pixelLines = lineCount * BitmapFont.GlyphHeight;
            if (pixelLines >= Height)
            {
                Clear(bgColor);
                return;
            }

            int shiftPixels = pixelLines * Width;
            int remainingPixels = (Height - pixelLines) * Width;

            // Copy rows up
            for (int i = 0; i < remainingPixels; i++)
            {
                Pixels[i] = Pixels[i + shiftPixels];
            }

            // Clear bottom rows
            for (int i = remainingPixels; i < Width * Height; i++)
            {
                Pixels[i] = bgColor;
            }
        }
    }
}
