namespace System.Runtime
{
    public static unsafe class RuntimeImports
    {
        [CompilerServices.MethodImpl(CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static void Memmove(byte* dest, byte* src, nuint len)
        {
            if (dest < src)
            {
                for (nuint i = 0; i < len; i++)
                {
                    dest[i] = src[i];
                }
            }
            else if (dest > src)
            {
                for (nuint i = len; i > 0; i--)
                {
                    dest[i - 1] = src[i - 1];
                }
            }
        }

        [CompilerServices.MethodImpl(CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static void Memset(byte* dest, byte value, nuint len)
        {
            for (nuint i = 0; i < len; i++)
            {
                dest[i] = value;
            }
        }

        [CompilerServices.MethodImpl(CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static void Memcpy(byte* dest, byte* src, nuint len)
        {
            for (nuint i = 0; i < len; i++)
            {
                dest[i] = src[i];
            }
        }
    }
}
