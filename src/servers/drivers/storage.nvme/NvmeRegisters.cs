namespace StorageNvme
{
    public static class NvmeRegisters
    {
        public const uint CAP   = 0x00; // Controller Capabilities (64-bit)
        public const uint VS    = 0x08; // Version (32-bit)
        public const uint INTMS = 0x0C; // Interrupt Mask Set (32-bit)
        public const uint INTMC = 0x10; // Interrupt Mask Clear (32-bit)
        public const uint CC    = 0x14; // Controller Configuration (32-bit)
        public const uint CSTS  = 0x1C; // Controller Status (32-bit)
        public const uint NSSR  = 0x20; // NVM Subsystem Reset (32-bit)
        public const uint AQA   = 0x24; // Admin Queue Attributes (32-bit)
        public const uint ASQ   = 0x28; // Admin Submission Queue Base (64-bit)
        public const uint ACQ   = 0x30; // Admin Completion Queue Base (64-bit)

        // CC bitfields
        public const uint CC_EN     = 1U << 0;
        public const uint CC_CSS_NVM = 0U << 4;
        public const uint CC_MPS_4K  = 0U << 7;
        public const uint CC_IOSQES_64 = 6U << 16;
        public const uint CC_IOCQES_16 = 4U << 20;

        // CSTS bitfields
        public const uint CSTS_RDY = 1U << 0;
        public const uint CSTS_CFS = 1U << 1;

        public static uint GetDoorbellOffset(uint qid, bool isCq, uint dstrd)
        {
            return 0x1000U + (2U * qid + (isCq ? 1U : 0U)) * (4U << (int)dstrd);
        }
    }
}
