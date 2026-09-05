namespace Microkernel.Drawing
{
    public static unsafe class BitmapFont
    {
        public const int GlyphWidth = 8;
        public const int GlyphHeight = 16;
        public const int BytesPerGlyph = 16;

        public static byte* FontDataPtr = null;

        public static void Initialize(byte* fontData)
        {
            FontDataPtr = fontData;
        }

        public static byte* GetGlyph(char c)
        {
            if (FontDataPtr == null) return null;
            int idx = (int)(byte)c;
            return FontDataPtr + (idx * BytesPerGlyph);
        }
    }
}
