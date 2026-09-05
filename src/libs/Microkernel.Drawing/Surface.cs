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

        public static uint BlendPixel(uint srcColor, uint dstColor, byte alpha)
        {
            if (alpha == 0) return dstColor;
            if (alpha == 255) return srcColor;

            uint invAlpha = (uint)(255 - alpha);

            uint srcR = (srcColor >> 16) & 0xFF;
            uint srcG = (srcColor >> 8) & 0xFF;
            uint srcB = srcColor & 0xFF;

            uint dstR = (dstColor >> 16) & 0xFF;
            uint dstG = (dstColor >> 8) & 0xFF;
            uint dstB = dstColor & 0xFF;

            uint outR = ((srcR * alpha) + (dstR * invAlpha)) / 255;
            uint outG = ((srcG * alpha) + (dstG * invAlpha)) / 255;
            uint outB = ((srcB * alpha) + (dstB * invAlpha)) / 255;

            return 0xFF000000 | (outR << 16) | (outG << 8) | outB;
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
                    byte alpha = set ? (byte)255 : (byte)0;

                    if (alpha > 0)
                    {
                        Pixels[rowOffset + px] = BlendPixel(fgColor, Pixels[rowOffset + px], alpha);
                    }
                    else if (bgColor != 0)
                    {
                        Pixels[rowOffset + px] = bgColor;
                    }
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
