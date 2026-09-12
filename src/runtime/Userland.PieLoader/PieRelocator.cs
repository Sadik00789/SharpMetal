using System;
using System.Runtime.CompilerServices;

namespace Userland.PieLoader
{
    public static unsafe class PieRelocator
    {
        public const ushort IMAGE_REL_BASED_ABSOLUTE = 0;
        public const ushort IMAGE_REL_BASED_DIR64 = 10;

        /// <summary>
        /// Relocates a PE image loaded at loadAddress when its preferred ImageBase differs.
        /// </summary>
        /// <param name="loadAddress">Actual base address in memory where the image is loaded</param>
        /// <param name="imageBase">Preferred ImageBase defined in the PE Optional Header</param>
        /// <param name="relocDirectoryRva">RVA of the Base Relocation Table (.reloc)</param>
        /// <param name="relocDirectorySize">Size in bytes of the Base Relocation Table</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ApplyRelocations(ulong loadAddress, ulong imageBase, uint relocDirectoryRva, uint relocDirectorySize)
        {
            if (loadAddress == 0 || relocDirectoryRva == 0 || relocDirectorySize == 0)
            {
                return true;
            }

            ulong imageDelta = loadAddress - imageBase;
            if (imageDelta == 0)
            {
                return true;
            }

            byte* relocPtr = (byte*)(loadAddress + relocDirectoryRva);
            byte* relocEnd = relocPtr + relocDirectorySize;

            while (relocPtr < relocEnd)
            {
                ImageBaseRelocation* block = (ImageBaseRelocation*)relocPtr;
                if (block->SizeOfBlock == 0) break;

                uint pageRva = block->VirtualAddress;
                uint entryCount = (block->SizeOfBlock - 8) / 2;
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
                        // Bind PE relocation patching to (loadAddress + pageRva + offset)
                        ulong* patchAddress = (ulong*)(loadAddress + pageRva + offset);
                        *patchAddress += imageDelta;
                    }
                }

                relocPtr += block->SizeOfBlock;
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
                    byte* dst = (byte*)(targetBaseAddress + vAddr);
                    for (uint b = 0; b < rawSize; b++) dst[b] = img[rawOff + b];
                    for (uint b = rawSize; b < vSize; b++) dst[b] = 0;
                }

                ApplyRelocations(targetBaseAddress, imageBase, relocRva, relocSize);
                return targetBaseAddress + entryRva;
            }
        }
    }
}
