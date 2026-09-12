namespace Internal.Runtime.CompilerHelpers
{
    public static class ThrowHelpers
    {
        public static void ThrowIndexOutOfRangeException()
        {
            while (true) { }
        }

        public static void ThrowOverflowException()
        {
            while (true) { }
        }

        public static void ThrowArgumentOutOfRangeException()
        {
            while (true) { }
        }

        public static void ThrowNullReferenceException()
        {
            while (true) { }
        }

        // ILCompiler RyuJIT TypeSystemThrowingILEmitter requires this stub when
        // analyzing assembly-load failure paths (e.g. shell -> Userland.PieLoader).
        // Freestanding: unsafe halt/panic loop, no EH, no allocation.
        public static void ThrowFileNotFoundException()
        {
            while (true) { }
        }
    }
}
