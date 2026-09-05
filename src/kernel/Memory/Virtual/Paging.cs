namespace Kernel.Memory.Virtual
{
    public static class Paging
    {
        public const ulong PageSize4K = 4096;
        public const ulong PageSize2M = 2 * 1024 * 1024;
        public const ulong PageSize1G = 1024 * 1024 * 1024;

        public const ulong Present = 1UL << 0;
        public const ulong Writable = 1UL << 1;
        public const ulong User = 1UL << 2;
        public const ulong WriteThrough = 1UL << 3;
        public const ulong CacheDisable = 1UL << 4;
        public const ulong Accessed = 1UL << 5;
        public const ulong Dirty = 1UL << 6;
        public const ulong LargePage = 1UL << 7; // For 2MB pages in Page Directory
        public const ulong Pat4K = 1UL << 7;     // PAT bit for 4KB page in Page Table
        public const ulong Pat2M = 1UL << 12;    // PAT bit for 2MB page in Page Directory
        public const ulong Global = 1UL << 8;
        public const ulong NoExecute = 1UL << 63;

        public const ulong AddressMask = 0x000F_FFFF_FFFF_F000UL;

        public static int GetPml4Index(ulong virt) => (int)((virt >> 39) & 0x1FF);
        public static int GetPdptIndex(ulong virt) => (int)((virt >> 30) & 0x1FF);
        public static int GetPdIndex(ulong virt)   => (int)((virt >> 21) & 0x1FF);
        public static int GetPtIndex(ulong virt)   => (int)((virt >> 12) & 0x1FF);
    }
}
