using System;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Elf;

namespace Userland.PieLoader
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
}
