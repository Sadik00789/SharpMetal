using System;
using Microkernel.Drawing;

namespace Shell
{
    public unsafe struct TerminalGrid
    {
        public const int Columns = 80;
        public const int Rows = 25;

        public fixed char Cells[Columns * Rows];
        public int CursorCol;
        public int CursorRow;

        public void Initialize()
        {
            CursorCol = 0;
            CursorRow = 0;
            for (int i = 0; i < Columns * Rows; i++)
            {
                Cells[i] = ' ';
            }
        }

        public void WriteChar(char c)
        {
            if (c == '\n')
            {
                CursorCol = 0;
                CursorRow++;
                if (CursorRow >= Rows)
                {
                    ScrollUp();
                    CursorRow = Rows - 1;
                }
                return;
            }

            if (c == '\b')
            {
                if (CursorCol > 0)
                {
                    CursorCol--;
                    Cells[CursorRow * Columns + CursorCol] = ' ';
                }
                return;
            }

            if (c == '\r')
            {
                CursorCol = 0;
                return;
            }

            Cells[CursorRow * Columns + CursorCol] = c;
            CursorCol++;
            if (CursorCol >= Columns)
            {
                CursorCol = 0;
                CursorRow++;
                if (CursorRow >= Rows)
                {
                    ScrollUp();
                    CursorRow = Rows - 1;
                }
            }
        }

        public void WriteString(string str)
        {
            if (str == null) return;
            for (int i = 0; i < str.Length; i++)
            {
                WriteChar(str[i]);
            }
        }

        public void ScrollUp()
        {
            // Shift rows up by 1
            for (int r = 0; r < Rows - 1; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    Cells[r * Columns + c] = Cells[(r + 1) * Columns + c];
                }
            }

            // Clear bottom row
            for (int c = 0; c < Columns; c++)
            {
                Cells[(Rows - 1) * Columns + c] = ' ';
            }
        }

        public void Render(ref Surface surface, uint fgColor, uint bgColor)
        {
            surface.Clear(bgColor);
            for (int r = 0; r < Rows; r++)
            {
                int py = r * BitmapFont.GlyphHeight;
                for (int c = 0; c < Columns; c++)
                {
                    char ch = Cells[r * Columns + c];
                    if (ch != ' ' && ch != 0)
                    {
                        int px = c * BitmapFont.GlyphWidth;
                        surface.DrawChar(px, py, ch, fgColor, bgColor);
                    }
                }
            }

            // Draw cursor block/underscore
            if (CursorRow < Rows && CursorCol < Columns)
            {
                int curX = CursorCol * BitmapFont.GlyphWidth;
                int curY = CursorRow * BitmapFont.GlyphHeight + 14;
                surface.DrawRect(curX, curY, BitmapFont.GlyphWidth, 2, Color32.Prompt);
            }
        }
    }
}
