using System;
using System.Runtime.InteropServices;

namespace Microkernel.Abstractions.Elf
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Elf64_Ehdr
    {
        public fixed byte e_ident[16];
        public ushort e_type;
        public ushort e_machine;
        public uint   e_version;
        public ulong  e_entry;
        public ulong  e_phoff;
        public ulong  e_shoff;
        public uint   e_flags;
        public ushort e_ehsize;
        public ushort e_phentsize;
        public ushort e_phnum;
        public ushort e_shentsize;
        public ushort e_shnum;
        public ushort e_shstrndx;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Elf64_Phdr
    {
        public uint  p_type;
        public uint  p_flags;
        public ulong p_offset;
        public ulong p_vaddr;
        public ulong p_paddr;
        public ulong p_filesz;
        public ulong p_memsz;
        public ulong p_align;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Elf64_Dyn
    {
        public long  d_tag;
        public ulong d_val;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Elf64_Rela
    {
        public ulong r_offset;
        public ulong r_info;
        public long  r_addend;

        public ulong R_Sym => r_info >> 32;
        public uint R_Type => (uint)(r_info & 0xFFFFFFFFUL);
    }

    public static class ElfConstants
    {
        public const byte ELFMAG0 = 0x7F;
        public const byte ELFMAG1 = (byte)'E';
        public const byte ELFMAG2 = (byte)'L';
        public const byte ELFMAG3 = (byte)'F';
        public const byte ELFCLASS64 = 2;
        public const byte ELFDATA2LSB = 1;

        public const ushort ET_NONE = 0;
        public const ushort ET_REL  = 1;
        public const ushort ET_EXEC = 2;
        public const ushort ET_DYN  = 3;
        public const ushort ET_CORE = 4;

        public const ushort EM_X86_64 = 62;

        public const uint PT_NULL    = 0;
        public const uint PT_LOAD    = 1;
        public const uint PT_DYNAMIC = 2;
        public const uint PT_INTERP  = 3;
        public const uint PT_NOTE    = 4;
        public const uint PT_SHLIB   = 5;
        public const uint PT_PHDR    = 6;
        public const uint PT_TLS     = 7;

        public const uint PF_X = 1;
        public const uint PF_W = 2;
        public const uint PF_R = 4;

        public const long DT_NULL      = 0;
        public const long DT_PLTGOT    = 3;
        public const long DT_STRTAB    = 5;
        public const long DT_SYMTAB    = 6;
        public const long DT_RELA      = 7;
        public const long DT_RELASZ    = 8;
        public const long DT_RELAENT   = 9;
        public const long DT_STRSZ     = 10;
        public const long DT_SYMENT    = 11;
        public const long DT_RELACOUNT = 0x6ffffff9;

        public const uint R_X86_64_NONE     = 0;
        public const uint R_X86_64_64       = 1;
        public const uint R_X86_64_GLOB_DAT = 6;
        public const uint R_X86_64_JUMP_SLOT= 7;
        public const uint R_X86_64_RELATIVE = 8;

        // System V AMD64 ABI Auxiliary Vector Types
        public const ulong AT_NULL   = 0;
        public const ulong AT_IGNORE = 1;
        public const ulong AT_EXECFD = 2;
        public const ulong AT_PHDR   = 3;
        public const ulong AT_PHENT  = 4;
        public const ulong AT_PHNUM  = 5;
        public const ulong AT_PAGESZ = 6;
        public const ulong AT_BASE   = 7;
        public const ulong AT_FLAGS  = 8;
        public const ulong AT_ENTRY  = 9;
        public const ulong AT_NOTELF = 10;
        public const ulong AT_UID    = 11;
        public const ulong AT_EUID   = 12;
        public const ulong AT_GID    = 13;
        public const ulong AT_EGID   = 14;
        public const ulong AT_RANDOM = 25;
    }
}
