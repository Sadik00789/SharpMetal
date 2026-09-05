using System.Runtime.InteropServices;

namespace StorageNvme
{
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct NvmeSqe
    {
        public byte Opcode;
        public byte Flags;
        public ushort CommandId;
        public uint Nsid;
        public ulong Reserved;
        public ulong Metadata;
        public ulong Prp1;
        public ulong Prp2;
        public uint Cdw10;
        public uint Cdw11;
        public uint Cdw12;
        public uint Cdw13;
        public uint Cdw14;
        public uint Cdw15;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    public struct NvmeCqe
    {
        public uint CommandSpecific;
        public uint Reserved;
        public ushort SqHead;
        public ushort SqId;
        public ushort CommandId;
        public ushort Status; // bit 0: Phase Tag (P)
    }

    public static class NvmeOpcodes
    {
        // Admin Opcodes
        public const byte DeleteIOSubmissionQueue = 0x00;
        public const byte CreateIOSubmissionQueue = 0x01;
        public const byte GetLogPage              = 0x02;
        public const byte DeleteIOCompletionQueue = 0x04;
        public const byte CreateIOCompletionQueue = 0x05;
        public const byte Identify                = 0x06;
        public const byte SetFeatures             = 0x09;

        // NVM Command Set Opcodes
        public const byte Flush = 0x00;
        public const byte Write = 0x01;
        public const byte Read  = 0x02;
    }
}
