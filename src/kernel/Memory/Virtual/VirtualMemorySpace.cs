using System;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Memory.Physical;

namespace Kernel.Memory.Virtual
{
    public static unsafe class VirtualMemorySpace
    {
        public static ulong Pml4PhysicalAddress { get; private set; }

        public static ulong CreateKernelSpace(ulong* pageTableMemory, ulong gopFbPhys, ulong gopFbSize)
        {
            // Zero 13 pages (53,248 bytes = 6,656 ulongs)
            ulong totalWords = (13 * 4096) / sizeof(ulong);
            for (ulong i = 0; i < totalWords; i++)
            {
                pageTableMemory[i] = 0;
            }

            ulong basePhys = (ulong)pageTableMemory;

            ulong* pml4 = pageTableMemory + (0 * 512);
            ulong* idPdpt = pageTableMemory + (1 * 512);
            ulong* idPd0 = pageTableMemory + (2 * 512);
            ulong* idPd1 = pageTableMemory + (3 * 512);
            ulong* idPd2 = pageTableMemory + (4 * 512);
            ulong* idPd3 = pageTableMemory + (5 * 512);

            ulong* hhdmPdpt = pageTableMemory + (6 * 512);
            ulong* hhdmPd0 = pageTableMemory + (7 * 512);
            ulong* hhdmPd1 = pageTableMemory + (8 * 512);
            ulong* hhdmPd2 = pageTableMemory + (9 * 512);
            ulong* hhdmPd3 = pageTableMemory + (10 * 512);

            ulong* kernelHighPdpt = pageTableMemory + (11 * 512);
            ulong* kernelHighPd = pageTableMemory + (12 * 512);

            ulong idPdptPhys = basePhys + (1 * 4096);
            ulong idPd0Phys = basePhys + (2 * 4096);
            ulong idPd1Phys = basePhys + (3 * 4096);
            ulong idPd2Phys = basePhys + (4 * 4096);
            ulong idPd3Phys = basePhys + (5 * 4096);

            ulong hhdmPdptPhys = basePhys + (6 * 4096);
            ulong hhdmPd0Phys = basePhys + (7 * 4096);
            ulong hhdmPd1Phys = basePhys + (8 * 4096);
            ulong hhdmPd2Phys = basePhys + (9 * 4096);
            ulong hhdmPd3Phys = basePhys + (10 * 4096);

            ulong kernelHighPdptPhys = basePhys + (11 * 4096);
            ulong kernelHighPdPhys = basePhys + (12 * 4096);

            // 1. Identity Mapping (PML4[0] -> idPdpt)
            pml4[0] = idPdptPhys | Paging.Present | Paging.Writable;
            idPdpt[0] = idPd0Phys | Paging.Present | Paging.Writable;
            idPdpt[1] = idPd1Phys | Paging.Present | Paging.Writable;
            idPdpt[2] = idPd2Phys | Paging.Present | Paging.Writable;
            idPdpt[3] = idPd3Phys | Paging.Present | Paging.Writable;

            // 2. HHDM (PML4[256] -> hhdmPdpt, covering 0xFFFF_8000_0000_0000)
            pml4[256] = hhdmPdptPhys | Paging.Present | Paging.Writable;
            hhdmPdpt[0] = hhdmPd0Phys | Paging.Present | Paging.Writable;
            hhdmPdpt[1] = hhdmPd1Phys | Paging.Present | Paging.Writable;
            hhdmPdpt[2] = hhdmPd2Phys | Paging.Present | Paging.Writable;
            hhdmPdpt[3] = hhdmPd3Phys | Paging.Present | Paging.Writable;

            // 3. Kernel High Space (PML4[511] -> kernelHighPdpt, covering 0xFFFF_FFFF_8000_0000)
            pml4[511] = kernelHighPdptPhys | Paging.Present | Paging.Writable;
            // 0xFFFFFFFF80000000: PDPT index 510
            kernelHighPdpt[510] = kernelHighPdPhys | Paging.Present | Paging.Writable;

            // Populate Page Directories: 4 x 512 x 2 MiB = 4 GiB
            ulong gopFbEnd = gopFbPhys + gopFbSize;

            for (int pdIdx = 0; pdIdx < 4; pdIdx++)
            {
                ulong* curIdPd = (pdIdx == 0) ? idPd0 : (pdIdx == 1) ? idPd1 : (pdIdx == 2) ? idPd2 : idPd3;
                ulong* curHhdmPd = (pdIdx == 0) ? hhdmPd0 : (pdIdx == 1) ? hhdmPd1 : (pdIdx == 2) ? hhdmPd2 : hhdmPd3;

                for (int entryIdx = 0; entryIdx < 512; entryIdx++)
                {
                    ulong phys = (((ulong)pdIdx * 512) + (ulong)entryIdx) * Paging.PageSize2M;
                    ulong flags = Paging.Present | Paging.Writable | Paging.LargePage;

                    // If page intersects GOP Framebuffer, set PAT bit for Write-Combining (PA4)
                    ulong pageEnd = phys + Paging.PageSize2M;
                    if (gopFbSize > 0 && phys < gopFbEnd && pageEnd > gopFbPhys)
                    {
                        flags |= Paging.Pat2M; // Bit 12 = PAT for 2MB pages
                    }

                    curIdPd[entryIdx] = phys | flags;
                    curHhdmPd[entryIdx] = phys | flags;

                    // Also map first 1 GiB to kernel high space (PDPT[510])
                    if (pdIdx == 0 && entryIdx < 512)
                    {
                        kernelHighPd[entryIdx] = phys | flags;
                    }
                }
            }

            Pml4PhysicalAddress = basePhys;
            return basePhys;
        }

        private static void ZeroPage(ulong* page)
        {
            for (int i = 0; i < 512; i++)
            {
                page[i] = 0;
            }
        }

        public static void MapUserPage4K(ulong* pml4, ulong virt, ulong phys, ulong flags)
        {
            int pml4Idx = Paging.GetPml4Index(virt);
            int pdptIdx = Paging.GetPdptIndex(virt);
            int pdIdx   = Paging.GetPdIndex(virt);
            int ptIdx   = Paging.GetPtIndex(virt);

            // 1. PML4 -> PDPT
            ulong* pdpt;
            if ((pml4[pml4Idx] & Paging.Present) == 0)
            {
                ulong pdptPhys = PageFrameAllocator.AllocateFrame();
                pdpt = (ulong*)Hhdm.PhysicalToVirtual(pdptPhys);
                ZeroPage(pdpt);
                pml4[pml4Idx] = pdptPhys | Paging.Present | Paging.Writable | Paging.User;
            }
            else
            {
                pml4[pml4Idx] |= Paging.User | Paging.Writable;
                ulong pdptPhys = pml4[pml4Idx] & Paging.AddressMask;
                pdpt = (ulong*)Hhdm.PhysicalToVirtual(pdptPhys);
            }

            // 2. PDPT -> PD
            ulong* pd;
            if ((pdpt[pdptIdx] & Paging.Present) == 0)
            {
                ulong pdPhys = PageFrameAllocator.AllocateFrame();
                pd = (ulong*)Hhdm.PhysicalToVirtual(pdPhys);
                ZeroPage(pd);
                pdpt[pdptIdx] = pdPhys | Paging.Present | Paging.Writable | Paging.User;
            }
            else
            {
                pdpt[pdptIdx] |= Paging.User | Paging.Writable;
                ulong pdPhys = pdpt[pdptIdx] & Paging.AddressMask;
                pd = (ulong*)Hhdm.PhysicalToVirtual(pdPhys);
            }

            // 3. PD -> PT
            ulong* pt;
            if ((pd[pdIdx] & Paging.Present) == 0)
            {
                ulong ptPhys = PageFrameAllocator.AllocateFrame();
                pt = (ulong*)Hhdm.PhysicalToVirtual(ptPhys);
                ZeroPage(pt);
                pd[pdIdx] = ptPhys | Paging.Present | Paging.Writable | Paging.User;
            }
            else
            {
                pd[pdIdx] |= Paging.User | Paging.Writable;
                ulong ptPhys = pd[pdIdx] & Paging.AddressMask;
                pt = (ulong*)Hhdm.PhysicalToVirtual(ptPhys);
            }

            // 4. PT -> Leaf physical page
            pt[ptIdx] = (phys & Paging.AddressMask) | flags;
        }

        public static ulong CreateUserAddressSpace(
            ulong kernelPml4Phys,
            ulong entryVirt,
            byte* payload,
            ulong payloadSize,
            ulong userStackVirt,
            ulong userStackSize,
            out ulong userStackTop)
        {
            // 1. Allocate dedicated 4 KiB frame for user PML4
            ulong userPml4Phys = PageFrameAllocator.AllocateFrame();
            ulong* userPml4 = (ulong*)Hhdm.PhysicalToVirtual(userPml4Phys);
            ZeroPage(userPml4);

            // 2. Mirror supervisor higher-half (entries 256..511) from kernel PML4 (U/S = 0)
            ulong* kernelPml4 = (ulong*)Hhdm.PhysicalToVirtual(kernelPml4Phys);
            for (int i = 256; i < 512; i++)
            {
                userPml4[i] = kernelPml4[i];
            }

            // 3. Map binary payload at entryVirt (0x40000000)
            if (payloadSize > 0x40 && payload[0] == 0x4D && payload[1] == 0x5A) // 'M', 'Z'
            {
                uint e_lfanew = *(uint*)(payload + 0x3C);
                if (e_lfanew < payloadSize && *(uint*)(payload + e_lfanew) == 0x00004550) // 'P', 'E', 0, 0
                {
                    // Map headers (first page at entryVirt)
                    ulong hdrFramePhys = PageFrameAllocator.AllocateFrame();
                    byte* hdrFrameVirt = (byte*)Hhdm.PhysicalToVirtual(hdrFramePhys);
                    ZeroPage((ulong*)hdrFrameVirt);
                    ulong hdrCopy = payloadSize < 4096 ? payloadSize : 4096;
                    for (ulong b = 0; b < hdrCopy; b++) hdrFrameVirt[b] = payload[b];
                    MapUserPage4K(userPml4, entryVirt, hdrFramePhys, Paging.Present | Paging.Writable | Paging.User);

                    ushort numSections = *(ushort*)(payload + e_lfanew + 6);
                    ushort optSize = *(ushort*)(payload + e_lfanew + 20);
                    byte* secHeaders = payload + e_lfanew + 24 + optSize;

                    for (int i = 0; i < numSections; i++)
                    {
                        byte* sec = secHeaders + (i * 40);
                        uint vSize = *(uint*)(sec + 8);
                        uint vAddr = *(uint*)(sec + 12);
                        uint rawSize = *(uint*)(sec + 16);
                        uint rawOffset = *(uint*)(sec + 20);

                        uint allocSize = vSize > rawSize ? vSize : rawSize;
                        ulong secPages = (allocSize + 4095) / 4096;

                        for (ulong p = 0; p < secPages; p++)
                        {
                            ulong framePhys = PageFrameAllocator.AllocateFrame();
                            byte* frameVirt = (byte*)Hhdm.PhysicalToVirtual(framePhys);
                            ZeroPage((ulong*)frameVirt);

                            ulong fileOffset = rawOffset + (p * 4096);
                            if (fileOffset < rawOffset + rawSize && fileOffset < payloadSize)
                            {
                                ulong bytesLeft = (rawOffset + rawSize) - fileOffset;
                                ulong toCopy = bytesLeft > 4096 ? 4096 : bytesLeft;
                                for (ulong b = 0; b < toCopy; b++)
                                {
                                    frameVirt[b] = payload[fileOffset + b];
                                }
                            }

                            ulong pageVirt = entryVirt + vAddr + (p * 4096);
                            MapUserPage4K(userPml4, pageVirt, framePhys, Paging.Present | Paging.Writable | Paging.User);
                        }
                    }
                }
            }
            else
            {
                // Flat binary fallback
                ulong pagesNeeded = (payloadSize + 4095) / 4096;
                for (ulong p = 0; p < pagesNeeded; p++)
                {
                    ulong framePhys = PageFrameAllocator.AllocateFrame();
                    byte* frameVirt = (byte*)Hhdm.PhysicalToVirtual(framePhys);
                    ZeroPage((ulong*)frameVirt);

                    ulong offset = p * 4096;
                    ulong bytesToCopy = (offset + 4096 <= payloadSize) ? 4096 : (payloadSize - offset);
                    for (ulong b = 0; b < bytesToCopy; b++)
                    {
                        frameVirt[b] = payload[offset + b];
                    }

                    ulong pageVirt = entryVirt + offset;
                    MapUserPage4K(userPml4, pageVirt, framePhys, Paging.Present | Paging.Writable | Paging.User);
                }
            }

            // 4. Map user stack at userStackVirt (0x00007FFFF0000000, 16 KiB = 4 pages)
            ulong stackPages = (userStackSize + 4095) / 4096;
            for (ulong s = 0; s < stackPages; s++)
            {
                ulong stackPhys = PageFrameAllocator.AllocateFrame();
                byte* stackVirt = (byte*)Hhdm.PhysicalToVirtual(stackPhys);
                ZeroPage((ulong*)stackVirt);

                ulong pageVirt = userStackVirt + (s * 4096);
                MapUserPage4K(userPml4, pageVirt, stackPhys, Paging.Present | Paging.Writable | Paging.User);
            }

            // 16-byte aligned user stack top
            userStackTop = (userStackVirt + userStackSize - 32) & ~15UL;

            return userPml4Phys;
        }

        public static void MapUserMmio(ulong userPml4Phys, ulong physAddr, ulong virtAddr, ulong sizeBytes, bool writeCombining)
        {
            if (userPml4Phys == 0)
            {
                userPml4Phys = Pml4PhysicalAddress;
            }

            // Constraint 4: Page-align both physAddr and virtAddr (& ~0xFFFUL) and round up sizeBytes to 4096-byte boundaries
            ulong alignedPhys = physAddr & ~0xFFFUL;
            ulong alignedVirt = virtAddr & ~0xFFFUL;
            ulong pageOffset = physAddr & 0xFFFUL;
            ulong alignedSize = (sizeBytes + pageOffset + 0xFFFUL) & ~0xFFFUL;
            if (alignedSize == 0)
            {
                alignedSize = 4096;
            }

            ulong flags = Paging.Present | Paging.Writable | Paging.User;
            if (writeCombining)
            {
                flags |= Paging.Pat4K; // PAT index 4: PAT=1, PCD=0, PWT=0 (Write-Combining)
            }
            else
            {
                flags |= Paging.CacheDisable; // PCD=1 (Uncacheable)
            }

            ulong* userPml4 = (ulong*)Hhdm.PhysicalToVirtual(userPml4Phys);
            bool isCurrentCr3 = Cpu.ReadCr3() == userPml4Phys;
            for (ulong offset = 0; offset < alignedSize; offset += 4096)
            {
                ulong pageVirt = alignedVirt + offset;
                MapUserPage4K(userPml4, pageVirt, alignedPhys + offset, flags);
                if (isCurrentCr3)
                {
                    InvalidatePage(pageVirt);
                }
            }

            // Invalidate TLB if modifying current CR3
            if (isCurrentCr3)
            {
                Cpu.WriteCr3(userPml4Phys);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static void InvalidatePage(ulong virtAddr)
        {
            Cpu.Invlpg(virtAddr);
        }

        public static ulong GetPhysicalAddress(ulong pml4Phys, ulong virtAddr)
        {
            if (pml4Phys == 0)
            {
                pml4Phys = Pml4PhysicalAddress != 0 ? Pml4PhysicalAddress : Cpu.ReadCr3();
            }

            ulong pml4Idx = (virtAddr >> 39) & 0x1FF;
            ulong pdptIdx = (virtAddr >> 30) & 0x1FF;
            ulong pdIdx   = (virtAddr >> 21) & 0x1FF;
            ulong ptIdx   = (virtAddr >> 12) & 0x1FF;
            ulong pageOff = virtAddr & 0xFFF;

            // Translate physical PML4 to HHDM virtual pointer
            ulong* pml4 = (ulong*)(Hhdm.Base + (pml4Phys & ~0xFFFUL));
            if ((pml4[pml4Idx] & Paging.Present) == 0) return 0;

            ulong* pdpt = (ulong*)(Hhdm.Base + (pml4[pml4Idx] & ~0xFFFUL & 0x000F_FFFF_FFFF_F000UL));
            if ((pdpt[pdptIdx] & Paging.Present) == 0) return 0;

            ulong* pd = (ulong*)(Hhdm.Base + (pdpt[pdptIdx] & ~0xFFFUL & 0x000F_FFFF_FFFF_F000UL));
            if ((pd[pdIdx] & Paging.Present) == 0) return 0;

            // Handle 2MB large page if PS bit is set
            if ((pd[pdIdx] & 0x80) != 0)
            {
                return (pd[pdIdx] & ~0x1FFFFFUL) | (virtAddr & 0x1FFFFFUL);
            }

            ulong* pt = (ulong*)(Hhdm.Base + (pd[pdIdx] & ~0xFFFUL & 0x000F_FFFF_FFFF_F000UL));
            if ((pt[ptIdx] & Paging.Present) == 0) return 0;

            return (pt[ptIdx] & ~0xFFFUL & 0x000F_FFFF_FFFF_F000UL) | pageOff;
        }
    }
}
