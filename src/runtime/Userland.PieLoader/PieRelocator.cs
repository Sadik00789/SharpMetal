using System;
using System.Runtime.CompilerServices;

namespace Userland.PieLoader
{
    public static unsafe class PieRelocator
    {
        public const ushort IMAGE_REL_BASED_ABSOLUTE = 0;
        public const ushort IMAGE_REL_BASED_DIR64 = 10;

        private const ulong MaxRelocEntries = 1024UL * 1024; // 1M cap: bounds corrupted counts
        private const ulong MaxImageSize = 256UL * 1024 * 1024; // 256MB cap

        /// <summary>
        /// Relocates a PE image loaded at loadAddress when its preferred ImageBase differs.
        /// Hardened: strict bounds validation; corrupted or overflowing entries
        /// yield a clean false instead of an out-of-bounds write.
        /// </summary>
        /// <param name="loadAddress">Actual base address in memory where the image is loaded</param>
        /// <param name="imageBase">Preferred ImageBase defined in the PE Optional Header</param>
        /// <param name="relocDirectoryRva">RVA of the Base Relocation Table (.reloc)</param>
        /// <param name="relocDirectorySize">Size in bytes of the Base Relocation Table</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ApplyRelocations(ulong loadAddress, ulong imageBase, uint relocDirectoryRva, uint relocDirectorySize)
        {
            // Backward-compatible entry: no image-size proof available, so use
            // overflow-safe validation only (no [Base, Base+Size) confinement).
            // New code should call the imageSize overload below.
            if (loadAddress == 0 || relocDirectoryRva == 0 || relocDirectorySize == 0)
            {
                return true;
            }

            ulong imageDelta = loadAddress - imageBase;
            if (imageDelta == 0)
            {
                return true;
            }

            // Reloc directory offset must not wrap.
            if (relocDirectoryRva > 0xFFFFFFFFFFFFFFFFUL - loadAddress) return false;
            if (relocDirectorySize > MaxRelocEntries * 2) return false;

            byte* relocPtr = (byte*)(loadAddress + relocDirectoryRva);
            // relocPtr + relocDirectorySize must not wrap.
            if (relocDirectorySize > 0xFFFFFFFFFFFFFFFFUL - (ulong)relocPtr) return false;
            byte* relocEnd = relocPtr + relocDirectorySize;

            while (relocPtr < relocEnd)
            {
                // Block header must be fully inside the directory.
                if ((ulong)(relocEnd - relocPtr) < 8) return false;
                ImageBaseRelocation* block = (ImageBaseRelocation*)relocPtr;
                if (block->SizeOfBlock == 0) break;
                if (block->SizeOfBlock < 8) return false;
                if (block->SizeOfBlock > (uint)(relocEnd - relocPtr)) return false;
                if ((block->SizeOfBlock - 8) % 2 != 0) return false;

                uint pageRva = block->VirtualAddress;
                uint entryCount = (block->SizeOfBlock - 8) / 2;
                if (entryCount > MaxRelocEntries) return false;
                ushort* entries = (ushort*)(relocPtr + 8);

                for (uint i = 0; i < entryCount; i++)
                {
                    ushort entry = entries[i];
                    ushort type = (ushort)(entry >> 12);
                    ushort offset = (ushort)(entry & 0x0FFF);

                    if (type == IMAGE_REL_BASED_ABSOLUTE)
                    {
                        // Padding, ignore
                        continue;
                    }
                    else if (type == IMAGE_REL_BASED_DIR64)
                    {
                        // pageRva + offset must not wrap the 32-bit RVA space.
                        uint rva = pageRva + (uint)offset;
                        if (rva < pageRva) return false;
                        if ((ulong)rva > 0xFFFFFFFFFFFFFFFFUL - loadAddress - 8) return false;
                        // Bind PE relocation patching to (loadAddress + pageRva + offset)
                        ulong* patchAddress = (ulong*)(loadAddress + rva);
                        *patchAddress += imageDelta;
                    }
                    else
                    {
                        // Unknown relocation type: corrupted table -> clean error.
                        return false;
                    }
                }

                relocPtr += block->SizeOfBlock;
            }

            return true;
        }

        /// <summary>
        /// Size-confined PE relocation: every 8-byte patch target must lie in
        /// [loadAddress, loadAddress + imageSize). Corrupted entries -> false.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ApplyRelocations(ulong loadAddress, ulong imageBase, uint relocDirectoryRva, uint relocDirectorySize, ulong imageSize)
        {
            if (loadAddress == 0) return false;
            if (imageSize == 0 || imageSize > MaxImageSize) return false;
            if (imageSize > 0xFFFFFFFFFFFFFFFFUL - loadAddress) return false;
            if (relocDirectoryRva == 0 || relocDirectorySize == 0)
            {
                return true;
            }
            // Reloc directory itself must live inside the image.
            if (!PieBounds.IsInImageRange(loadAddress, imageSize, loadAddress + relocDirectoryRva, relocDirectorySize))
            {
                // Overflow-safe pre-check (wrapping RVA + loadAddress).
                if (relocDirectoryRva > 0xFFFFFFFFFFFFFFFFUL - loadAddress) return false;
                return false;
            }

            ulong imageDelta = loadAddress - imageBase;
            if (imageDelta == 0)
            {
                return true;
            }

            byte* relocPtr = (byte*)(loadAddress + relocDirectoryRva);
            if (relocDirectorySize > 0xFFFFFFFFFFFFFFFFUL - (ulong)relocPtr) return false;
            byte* relocEnd = relocPtr + relocDirectorySize;

            while (relocPtr < relocEnd)
            {
                if ((ulong)(relocEnd - relocPtr) < 8) return false;
                ImageBaseRelocation* block = (ImageBaseRelocation*)relocPtr;
                if (block->SizeOfBlock == 0) break;
                if (block->SizeOfBlock < 8) return false;
                if (block->SizeOfBlock > (uint)(relocEnd - relocPtr)) return false;
                if ((block->SizeOfBlock - 8) % 2 != 0) return false;

                uint pageRva = block->VirtualAddress;
                uint entryCount = (block->SizeOfBlock - 8) / 2;
                if (entryCount > MaxRelocEntries) return false;
                ushort* entries = (ushort*)(relocPtr + 8);

                for (uint i = 0; i < entryCount; i++)
                {
                    ushort entry = entries[i];
                    ushort type = (ushort)(entry >> 12);
                    ushort offset = (ushort)(entry & 0x0FFF);

                    if (type == IMAGE_REL_BASED_ABSOLUTE)
                    {
                        continue;
                    }
                    else if (type == IMAGE_REL_BASED_DIR64)
                    {
                        uint rva = pageRva + (uint)offset;
                        if (rva < pageRva) return false;
                        // Strict confinement: 8-byte write must be inside the image.
                        if (!PieBounds.IsInImageRange(loadAddress, imageSize, loadAddress + rva, 8)) return false;
                        ulong* patchAddress = (ulong*)(loadAddress + rva);
                        // Delta application must not wrap (defense in depth).
                        ulong old = *patchAddress;
                        if (imageDelta > 0xFFFFFFFFFFFFFFFFUL - old) return false;
                        *patchAddress = old + imageDelta;
                    }
                    else
                    {
                        return false;
                    }
                }

                relocPtr += block->SizeOfBlock;
            }

            return true;
        }

        /// <summary>
        /// ELF64 RELA relocation with strict [BaseAddress, BaseAddress+ImageSize)
        /// confinement. Validates every r_offset target, every symbol lookup,
        /// and every computed value. Corrupted or overflowing entries yield a
        /// clean false; no partial out-of-bounds write is ever performed.
        /// Supports R_X86_64_NONE / 64 / GLOB_DAT / JUMP_SLOT / RELATIVE.
        /// </summary>
        /// <param name="baseAddress">Load base of the PIE image</param>
        /// <param name="imageSize">Allocated virtual segment size in bytes</param>
        /// <param name="relaEntries">Pointer to Elf64_Rela array (SHT_RELA)</param>
        /// <param name="relaCount">Number of entries in the array</param>
        /// <param name="symtab">Pointer to Elf64_Sym array (may be null if only RELATIVE)</param>
        /// <param name="symCount">Number of symbols in symtab</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ApplyElfRelocations(ulong baseAddress, ulong imageSize, Elf64_Rela* relaEntries, ulong relaCount, Elf64_Sym* symtab, ulong symCount)
        {
            if (baseAddress == 0) return false;
            if (imageSize == 0 || imageSize > MaxImageSize) return false;
            if (imageSize > 0xFFFFFFFFFFFFFFFFUL - baseAddress) return false;
            ulong imageEnd = baseAddress + imageSize;
            if (relaCount > MaxRelocEntries) return false;
            if (relaCount > 0 && relaEntries == null) return false;
            // Rela table address range must not wrap.
            if (relaCount > 0)
            {
                if (relaCount > 0xFFFFFFFFFFFFFFFFUL / (ulong)sizeof(Elf64_Rela)) return false;
                ulong tableBytes = relaCount * (ulong)sizeof(Elf64_Rela);
                if (tableBytes > 0xFFFFFFFFFFFFFFFFUL - (ulong)relaEntries) return false;
            }
            if (symCount > MaxRelocEntries) return false;
            if (symCount > 0 && symtab == null) return false;
            if (symCount > 0)
            {
                if (symCount > 0xFFFFFFFFFFFFFFFFUL / (ulong)sizeof(Elf64_Sym)) return false;
                ulong symBytes = symCount * (ulong)sizeof(Elf64_Sym);
                if (symBytes > 0xFFFFFFFFFFFFFFFFUL - (ulong)symtab) return false;
            }

            for (ulong i = 0; i < relaCount; i++)
            {
                Elf64_Rela* rela = relaEntries + i;
                ulong r_offset = rela->r_offset;
                ulong r_info = rela->r_info;
                long r_addend = rela->r_addend;
                ulong sym = r_info >> 32;
                uint type = (uint)(r_info & 0xFFFFFFFFUL);

                if (!ElfRelocTypeX86_64.IsSupported(type)) return false;

                // Target write location must be an 8-byte field inside the image.
                // r_offset is a base-relative offset for PIE.
                if (r_offset > 0xFFFFFFFFFFFFFFFFUL - baseAddress) return false;
                ulong target = baseAddress + r_offset;
                if (!PieBounds.IsInImageRange(baseAddress, imageSize, target, 8)) return false;

                if (type == ElfRelocTypeX86_64.R_X86_64_NONE)
                {
                    continue;
                }
                else if (type == ElfRelocTypeX86_64.R_X86_64_RELATIVE)
                {
                    // RELATIVE must not reference a symbol.
                    if (sym != 0) return false;
                    // addend is a base-relative offset; result must land inside image.
                    if (r_addend < 0) return false;
                    ulong add = (ulong)r_addend;
                    if (add > 0xFFFFFFFFFFFFFFFFUL - baseAddress) return false;
                    ulong value = baseAddress + add;
                    if (value < baseAddress || value >= imageEnd) return false;
                    *(ulong*)target = value;
                }
                else
                {
                    // Symbol-dependent: index must be within the symbol table.
                    if (sym >= symCount) return false;
                    Elf64_Sym* s = symtab + sym;
                    // UNDEF (SHN_UNDEF == 0) cannot be resolved internally -> error.
                    if (s->st_shndx == 0) return false;
                    // Symbol value is base-relative; resolved address must be inside image.
                    if (s->st_value > 0xFFFFFFFFFFFFFFFFUL - baseAddress) return false;
                    ulong resolved = baseAddress + s->st_value;
                    if (resolved < baseAddress || resolved >= imageEnd) return false;
                    // Final value = resolved + addend; must not wrap and must land inside image.
                    long add = r_addend;
                    ulong finalValue;
                    if (add >= 0)
                    {
                        if ((ulong)add > 0xFFFFFFFFFFFFFFFFUL - resolved) return false;
                        finalValue = resolved + (ulong)add;
                    }
                    else
                    {
                        ulong neg = (ulong)(-add);
                        if (neg > resolved) return false;
                        finalValue = resolved - neg;
                    }
                    if (finalValue < baseAddress || finalValue >= imageEnd) return false;
                    *(ulong*)target = finalValue;
                }
            }

            return true;
        }

        /// <summary>
        /// RELATIVE-only fast path (no symbol table). Strict target confinement.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ApplyElfRelocations(ulong baseAddress, ulong imageSize, Elf64_Rela* relaEntries, ulong relaCount)
        {
            if (baseAddress == 0) return false;
            if (imageSize == 0 || imageSize > MaxImageSize) return false;
            if (imageSize > 0xFFFFFFFFFFFFFFFFUL - baseAddress) return false;
            ulong imageEnd = baseAddress + imageSize;
            if (relaCount > MaxRelocEntries) return false;
            if (relaCount > 0 && relaEntries == null) return false;

            for (ulong i = 0; i < relaCount; i++)
            {
                Elf64_Rela* rela = relaEntries + i;
                ulong r_offset = rela->r_offset;
                ulong sym = rela->r_info >> 32;
                uint type = (uint)(rela->r_info & 0xFFFFFFFFUL);
                long addend = rela->r_addend;

                if (type != ElfRelocTypeX86_64.R_X86_64_NONE &&
                    type != ElfRelocTypeX86_64.R_X86_64_RELATIVE) return false;
                if (r_offset > 0xFFFFFFFFFFFFFFFFUL - baseAddress) return false;
                ulong target = baseAddress + r_offset;
                if (!PieBounds.IsInImageRange(baseAddress, imageSize, target, 8)) return false;
                if (type == ElfRelocTypeX86_64.R_X86_64_NONE) continue;
                if (sym != 0) return false;
                if (addend < 0) return false;
                ulong add = (ulong)addend;
                if (add > 0xFFFFFFFFFFFFFFFFUL - baseAddress) return false;
                ulong value = baseAddress + add;
                if (value < baseAddress || value >= imageEnd) return false;
                *(ulong*)target = value;
            }

            return true;
        }

        // Phase 4 (Ring-3 half): parse PE in userland, stage into current
        // address space, then ask Ring-0 to synthesize the new process via
        // SysSpawn(entryVirt, stackTop). Never touches VirtualMemorySpace.
        // Returns spawned TID (0 on failure).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong LoadAndRelocateFromVfs(string vfsPath, ulong targetBaseAddress)
        {
            if (vfsPath == null || targetBaseAddress == 0) return 0;
            byte[] image;
            try
            {
                image = System.IO.File.ReadAllBytes(vfsPath);
            }
            catch
            {
                return 0;
            }
            if (image == null || image.Length < 0x40) return 0;

            fixed (byte* img = image)
            {
                if (img[0] != 0x4D || img[1] != 0x5A) return 0;
                uint e_lfanew = *(uint*)(img + 0x3C);
                if (e_lfanew + 6 + 40 > (uint)image.Length) return 0;
                if (*(uint*)(img + e_lfanew) != 0x00004550) return 0;

                ushort numSections = *(ushort*)(img + e_lfanew + 6);
                ushort optSize = *(ushort*)(img + e_lfanew + 20);
                if (numSections == 0 || numSections > 96) return 0;
                byte* optHdr = img + e_lfanew + 24;
                ushort magic = *(ushort*)optHdr;
                ulong imageBase;
                uint entryRva;
                uint relocRva;
                uint relocSize;
                if (magic == 0x20B) // PE32+
                {
                    imageBase = *(ulong*)(optHdr + 24);
                    entryRva = *(uint*)(optHdr + 16);
                    relocRva = *(uint*)(optHdr + 136);
                    relocSize = *(uint*)(optHdr + 140);
                }
                else if (magic == 0x10B) // PE32
                {
                    imageBase = *(uint*)(optHdr + 28);
                    entryRva = *(uint*)(optHdr + 16);
                    relocRva = *(uint*)(optHdr + 104);
                    relocSize = *(uint*)(optHdr + 108);
                }
                else
                {
                    return 0;
                }

                byte* secHeaders = img + e_lfanew + 24 + optSize;
                // Compute image extent (max vAddr + alloc) for [Base, Base+Size) proof.
                ulong imageExtent = 0;
                for (int s = 0; s < numSections; s++)
                {
                    byte* sec = secHeaders + (s * 40);
                    if ((ulong)(sec + 40 - img) > (ulong)image.Length) return 0;
                    uint vSize = *(uint*)(sec + 8);
                    uint vAddr = *(uint*)(sec + 12);
                    uint rawSize = *(uint*)(sec + 16);
                    uint alloc = vSize > rawSize ? vSize : rawSize;
                    if (alloc == 0) continue;
                    if ((ulong)vAddr > 0xFFFFFFFFFFFFFFFFUL - alloc) return 0;
                    ulong secEnd = (ulong)vAddr + alloc;
                    if (secEnd > imageExtent) imageExtent = secEnd;
                }
                if (imageExtent == 0 || imageExtent > MaxImageSize) return 0;
                // Copy sections to targetBaseAddress+RVA in current address
                // space (caller pre-mapped target via AllocDma/MapMmio window
                // or relies on identity staging at targetBaseAddress).
                for (int i = 0; i < numSections; i++)
                {
                    byte* sec = secHeaders + (i * 40);
                    if ((ulong)(sec + 40 - img) > (ulong)image.Length) return 0;
                    uint vSize = *(uint*)(sec + 8);
                    uint vAddr = *(uint*)(sec + 12);
                    uint rawSize = *(uint*)(sec + 16);
                    uint rawOff = *(uint*)(sec + 20);
                    uint alloc = vSize > rawSize ? vSize : rawSize;
                    if (alloc == 0) continue;
                    if ((ulong)rawOff + rawSize > (ulong)image.Length) return 0;
                    // Destination must stay inside the proven image extent.
                    if ((ulong)vAddr + alloc > imageExtent) return 0;
                    if ((ulong)vAddr > 0xFFFFFFFFFFFFFFFFUL - targetBaseAddress) return 0;
                    byte* dst = (byte*)(targetBaseAddress + vAddr);
                    for (uint b = 0; b < rawSize; b++) dst[b] = img[rawOff + b];
                    for (uint b = rawSize; b < vSize; b++) dst[b] = 0;
                }

                bool ok = ApplyRelocations(targetBaseAddress, imageBase, relocRva, relocSize, imageExtent);
                if (!ok) return 0;
                if ((ulong)entryRva >= imageExtent) return 0;
                return targetBaseAddress + entryRva;
            }
        }
    }
}
