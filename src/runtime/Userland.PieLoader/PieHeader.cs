using System.Runtime.InteropServices;

namespace Userland.PieLoader
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ImageDosHeader
    {
        public ushort e_magic;
        public ushort e_cblp;
        public ushort e_cp;
        public ushort e_crlc;
        public ushort e_cparhdr;
        public ushort e_minalloc;
        public ushort e_maxalloc;
        public ushort e_ss;
        public ushort e_sp;
        public ushort e_csum;
        public ushort e_ip;
        public ushort e_cs;
        public ushort e_lfarlc;
        public ushort e_ovno;
        public fixed ushort e_res[4];
        public ushort e_oemid;
        public ushort e_oeminfo;
        public fixed ushort e_res2[10];
        public int e_lfanew;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ImageBaseRelocation
    {
        public uint VirtualAddress;
        public uint SizeOfBlock;
    }

    /// <summary>
    /// ELF64 relocation with addend (SHT_RELA entry), x86-64 System V ABI.
    /// r_offset: location to patch (offset from BaseAddress for PIE).
    /// r_info:   (sym << 32) | type.
    /// r_addend: signed addend applied during relocation.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Elf64_Rela
    {
        public ulong r_offset;
        public ulong r_info;
        public long r_addend;

        public ulong R_Sym => r_info >> 32;
        public uint R_Type => (uint)(r_info & 0xFFFFFFFFUL);
    }

    /// <summary>ELF64 symbol table entry (SysV ABI, 24 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Elf64_Sym
    {
        public uint st_name;
        public byte st_info;
        public byte st_other;
        public ushort st_shndx;
        public ulong st_value;
        public ulong st_size;
    }

    /// <summary>x86-64 relocation types (subset supported by the PIE loader).</summary>
    public static class ElfRelocTypeX86_64
    {
        public const uint R_X86_64_NONE = 0;
        public const uint R_X86_64_64 = 1;
        public const uint R_X86_64_GLOB_DAT = 6;
        public const uint R_X86_64_JUMP_SLOT = 7;
        public const uint R_X86_64_RELATIVE = 8;

        public static bool IsSupported(uint type)
        {
            return type == R_X86_64_NONE
                || type == R_X86_64_64
                || type == R_X86_64_GLOB_DAT
                || type == R_X86_64_JUMP_SLOT
                || type == R_X86_64_RELATIVE;
        }
    }

    /// <summary>
    /// Shared bounds helper: proves [address, address+accessSize) lies wholly
    /// inside [baseAddress, baseAddress+imageSize). Rejects zero image size,
    /// wrapping arithmetic, and out-of-range accesses with a clean false.
    /// </summary>
    public static class PieBounds
    {
        public static bool IsInImageRange(ulong baseAddress, ulong imageSize, ulong address, ulong accessSize)
        {
            if (imageSize == 0) return false;
            if (accessSize == 0) return false;
            if (imageSize > 0xFFFFFFFFFFFFFFFFUL - baseAddress) return false;
            ulong imageEnd = baseAddress + imageSize;
            if (address < baseAddress) return false;
            if (accessSize > 0xFFFFFFFFFFFFFFFFUL - address) return false;
            if (address + accessSize > imageEnd) return false;
            return true;
        }
    }
}
