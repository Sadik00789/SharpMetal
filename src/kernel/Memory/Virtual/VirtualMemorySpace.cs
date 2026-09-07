using System;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Memory.Physical;

namespace Kernel.Memory.Virtual
{
    public static unsafe class VirtualMemorySpace
    {
        public static ulong Pml4PhysicalAddress { get; private set; }

        public const int NumPds = 16; // 16 x 1 GiB = 16 GiB covered

        public static ulong CreateKernelSpace(ulong* pageTableMemory, ulong gopFbPhys, ulong gopFbSize)
        {
            // Zero 38 pages (155,648 bytes)
            ulong totalWords = (38 * 4096) / sizeof(ulong);
            for (ulong i = 0; i < totalWords; i++)
            {
                pageTableMemory[i] = 0;
            }

            ulong basePhys = (ulong)pageTableMemory;

            // Layout:
            // Page 0: PML4
            // Page 1: Identity PDPT
            // Pages 2..17: Identity PDs (16 pages)
            // Page 18: HHDM PDPT
            // Pages 19..34: HHDM PDs (16 pages)
            // Page 35: KernelHigh PDPT
            // Page 36: KernelHigh PD

            ulong* pml4 = pageTableMemory + (0 * 512);
            ulong* idPdpt = pageTableMemory + (1 * 512);
            ulong* hhdmPdpt = pageTableMemory + (18 * 512);
            ulong* kernelHighPdpt = pageTableMemory + (35 * 512);
            ulong* kernelHighPd = pageTableMemory + (36 * 512);

            ulong idPdptPhys = basePhys + (1 * 4096);
            ulong hhdmPdptPhys = basePhys + (18 * 4096);
            ulong kernelHighPdptPhys = basePhys + (35 * 4096);
            ulong kernelHighPdPhys = basePhys + (36 * 4096);

            // 1. PML4 Links
            pml4[0] = idPdptPhys | Paging.Present | Paging.Writable;
            pml4[256] = hhdmPdptPhys | Paging.Present | Paging.Writable;
            pml4[511] = kernelHighPdptPhys | Paging.Present | Paging.Writable;

            // Kernel high space (0xFFFFFFFF80000000: PDPT index 510)
            kernelHighPdpt[510] = kernelHighPdPhys | Paging.Present | Paging.Writable;

            ulong gopFbEnd = gopFbPhys + gopFbSize;

            // 2. Populate 16 Identity and 16 HHDM Page Directories (16 GiB total)
            for (int pdIdx = 0; pdIdx < NumPds; pdIdx++)
            {
                ulong idPdPhys = basePhys + ((2 + (ulong)pdIdx) * 4096);
                ulong* curIdPd = pageTableMemory + ((2 + pdIdx) * 512);
                idPdpt[pdIdx] = idPdPhys | Paging.Present | Paging.Writable;

                ulong hhdmPdPhys = basePhys + ((19 + (ulong)pdIdx) * 4096);
                ulong* curHhdmPd = pageTableMemory + ((19 + pdIdx) * 512);
                hhdmPdpt[pdIdx] = hhdmPdPhys | Paging.Present | Paging.Writable;

                for (int entryIdx = 0; entryIdx < 512; entryIdx++)
                {
                    ulong phys = (((ulong)pdIdx * 512) + (ulong)entryIdx) * Paging.PageSize2M;
                    ulong flags = Paging.Present | Paging.Writable | Paging.LargePage;

                    // If page intersects GOP Framebuffer, set PAT bit for Write-Combining (PA4)
                    ulong pageEnd = phys + Paging.PageSize2M;
                    if (gopFbSize > 0 && phys < gopFbEnd && pageEnd > gopFbPhys)
                    {
                        flags |= Paging.Pat2M;
                    }

                    curIdPd[entryIdx] = phys | flags;
                    curHhdmPd[entryIdx] = phys | flags;

                    // Map first 1 GiB to kernel high space
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
            // Assert that userland page mappings never set PTE_GLOBAL (bit 8)
            flags &= ~Paging.Global;

            int pml4Idx = Paging.GetPml4Index(virt);
            int pdptIdx = Paging.GetPdptIndex(virt);
            int pdIdx   = Paging.GetPdIndex(virt);
            int ptIdx   = Paging.GetPtIndex(virt);

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
            ulong userPml4Phys = PageFrameAllocator.AllocateFrame();
            ulong* userPml4 = (ulong*)Hhdm.PhysicalToVirtual(userPml4Phys);
            ZeroPage(userPml4);

            ulong* kernelPml4 = (ulong*)Hhdm.PhysicalToVirtual(kernelPml4Phys);
            for (int i = 256; i < 512; i++)
            {
                userPml4[i] = kernelPml4[i];
            }

            if (payloadSize > 0x40 && payload[0] == 0x4D && payload[1] == 0x5A)
            {
                uint e_lfanew = *(uint*)(payload + 0x3C);
                if (e_lfanew < payloadSize && *(uint*)(payload + e_lfanew) == 0x00004550)
                {
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

            ulong stackPages = (userStackSize + 4095) / 4096;
            for (ulong s = 0; s < stackPages; s++)
            {
                ulong stackPhys = PageFrameAllocator.AllocateFrame();
                byte* stackVirt = (byte*)Hhdm.PhysicalToVirtual(stackPhys);
                ZeroPage((ulong*)stackVirt);

                ulong pageVirt = userStackVirt + (s * 4096);
                MapUserPage4K(userPml4, pageVirt, stackPhys, Paging.Present | Paging.Writable | Paging.User);
            }

            userStackTop = (userStackVirt + userStackSize - 32) & ~15UL;
            return userPml4Phys;
        }

        public static void MapUserMmio(ulong userPml4Phys, ulong physAddr, ulong virtAddr, ulong sizeBytes, bool writeCombining)
        {
            if (userPml4Phys == 0)
            {
                userPml4Phys = Pml4PhysicalAddress;
            }

            ulong alignedPhys = physAddr & ~0xFFFUL;
            ulong alignedVirt = virtAddr & ~0xFFFUL;
            ulong pageOffset = physAddr & 0xFFFUL;
            ulong alignedSize = (sizeBytes + pageOffset + 0xFFFUL) & ~0xFFFUL;
            if (alignedSize == 0) alignedSize = 4096;

            ulong flags = Paging.Present | Paging.Writable | Paging.User;
            if (writeCombining) flags |= Paging.Pat4K;
            else flags |= Paging.CacheDisable;

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

            if (isCurrentCr3)
            {
                Cpu.WriteCr3(userPml4Phys);
            }
        }

        public static unsafe void UnmapPage(ulong pml4Phys, ulong vaddr)
        {
            if (pml4Phys == 0) return;
            ulong* pml4 = (ulong*)Hhdm.PhysicalToVirtual(pml4Phys);
            ulong pml4Idx = (vaddr >> 39) & 0x1FF;
            if ((pml4[pml4Idx] & 1) == 0) return;

            ulong* pdpt = (ulong*)Hhdm.PhysicalToVirtual(pml4[pml4Idx] & 0x000F_FFFF_FFFF_F000UL);
            ulong pdptIdx = (vaddr >> 30) & 0x1FF;
            if ((pdpt[pdptIdx] & 1) == 0) return;

            ulong* pd = (ulong*)Hhdm.PhysicalToVirtual(pdpt[pdptIdx] & 0x000F_FFFF_FFFF_F000UL);
            ulong pdIdx = (vaddr >> 21) & 0x1FF;
            if ((pd[pdIdx] & 1) == 0) return;

            if ((pd[pdIdx] & Paging.LargePage) != 0)
            {
                // If unmapping low trampoline address 0x0000_8000, split the 2MB page into 4KB entries
                if (vaddr < Paging.PageSize2M)
                {
                    ulong ptPhys = PageFrameAllocator.AllocateFrame();
                    if (ptPhys != 0)
                    {
                        ulong* newPt = (ulong*)Hhdm.PhysicalToVirtual(ptPhys);
                        for (uint i = 0; i < 512; i++)
                        {
                            ulong phys = (ulong)i * 4096;
                            if (phys == (vaddr & ~0xFFFUL))
                            {
                                newPt[i] = 0; // Unmap 0x8000
                            }
                            else
                            {
                                newPt[i] = phys | Paging.Present | Paging.Writable;
                            }
                        }
                        pd[pdIdx] = ptPhys | Paging.Present | Paging.Writable;
                    }
                }
                else
                {
                    pd[pdIdx] = 0;
                }
            }
            else
            {
                ulong* pt = (ulong*)Hhdm.PhysicalToVirtual(pd[pdIdx] & 0x000F_FFFF_FFFF_F000UL);
                ulong ptIdx = (vaddr >> 12) & 0x1FF;
                pt[ptIdx] = 0; // Clear PTE
            }

            // Issue broadcast TLB shootdown across all cores
            SmpTlbShootdown.BroadcastShootdown(pml4Phys, vaddr, 1);
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

            ulong* pml4 = (ulong*)(Hhdm.Base + (pml4Phys & ~0xFFFUL));
            if ((pml4[pml4Idx] & Paging.Present) == 0) return 0;

            ulong* pdpt = (ulong*)(Hhdm.Base + (pml4[pml4Idx] & ~0xFFFUL & 0x000F_FFFF_FFFF_F000UL));
            if ((pdpt[pdptIdx] & Paging.Present) == 0) return 0;

            ulong* pd = (ulong*)(Hhdm.Base + (pdpt[pdptIdx] & ~0xFFFUL & 0x000F_FFFF_FFFF_F000UL));
            if ((pd[pdIdx] & Paging.Present) == 0) return 0;

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
