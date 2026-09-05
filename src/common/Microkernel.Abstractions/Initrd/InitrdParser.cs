using System;
using System.Runtime.InteropServices;

namespace Microkernel.Abstractions.Initrd
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct InitrdHeader
    {
        public ulong Magic; // 0x445254494E49534F ('OSINITRD')
        public uint EntryCount;
        public uint ArchiveSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct InitrdEntry
    {
        public fixed byte Name[32];
        public ulong Offset;
        public ulong Length;
    }

    public static unsafe class InitrdParser
    {
        public const ulong ExpectedMagic = 0x445254494E49534FUL; // 'OSINITRD'

        public static bool FindEntry(
            byte* initrdBase,
            ulong initrdSize,
            string name,
            out byte* payload,
            out ulong payloadSize)
        {
            payload = null;
            payloadSize = 0;

            if (initrdBase == null || initrdSize < (ulong)sizeof(InitrdHeader))
            {
                return false;
            }

            InitrdHeader* hdr = (InitrdHeader*)initrdBase;
            if (hdr->Magic != ExpectedMagic)
            {
                return false;
            }

            InitrdEntry* entries = (InitrdEntry*)(initrdBase + sizeof(InitrdHeader));

            for (uint i = 0; i < hdr->EntryCount; i++)
            {
                InitrdEntry* e = &entries[i];

                bool match = true;
                int nameLen = name.Length;
                for (int j = 0; j < nameLen && j < 32; j++)
                {
                    if (e->Name[j] != (byte)name[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match && (nameLen >= 32 || e->Name[nameLen] == 0))
                {
                    if (e->Offset + e->Length <= initrdSize)
                    {
                        payload = initrdBase + e->Offset;
                        payloadSize = e->Length;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
