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
    }
}
