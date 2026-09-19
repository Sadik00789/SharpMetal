using System;
using Microkernel.Abstractions.Elf;

namespace Userland.PieLoader
{
    public static unsafe class PieLoader
    {
        public static bool ValidateElfHeader(Elf64_Ehdr* ehdr)
        {
            if (ehdr == null) return false;

            if (ehdr->e_ident[0] != ElfConstants.ELFMAG0 ||
                ehdr->e_ident[1] != ElfConstants.ELFMAG1 ||
                ehdr->e_ident[2] != ElfConstants.ELFMAG2 ||
                ehdr->e_ident[3] != ElfConstants.ELFMAG3)
            {
                return false;
            }

            if (ehdr->e_ident[4] != ElfConstants.ELFCLASS64) return false;
            if (ehdr->e_ident[5] != ElfConstants.ELFDATA2LSB) return false;
            if (ehdr->e_type != ElfConstants.ET_DYN) return false;
            if (ehdr->e_machine != ElfConstants.EM_X86_64) return false;

            return true;
        }

        public static Elf64_Phdr* GetProgramHeaders(Elf64_Ehdr* ehdr)
        {
            if (ehdr == null || ehdr->e_phoff == 0) return null;
            return (Elf64_Phdr*)((byte*)ehdr + ehdr->e_phoff);
        }

        public static bool FindSegmentByType(Elf64_Ehdr* ehdr, uint segmentType, out Elf64_Phdr foundPhdr)
        {
            foundPhdr = default;
            if (!ValidateElfHeader(ehdr)) return false;

            Elf64_Phdr* phdrs = GetProgramHeaders(ehdr);
            if (phdrs == null) return false;

            for (ushort i = 0; i < ehdr->e_phnum; i++)
            {
                if (phdrs[i].p_type == segmentType)
                {
                    foundPhdr = phdrs[i];
                    return true;
                }
            }

            return false;
        }
    }
}
