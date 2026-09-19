using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Concurrency;
using Kernel.Memory.Physical;
using Kernel.Scheduling;

namespace Kernel.Memory.Virtual
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct VirtualMemoryRegion
    {
        public ulong Start;
        public ulong End;
        public uint Protection;
        public uint BackingType;
        public uint FileHandle;
        public ulong FileOffset;
        public VirtualMemoryRegion* Next;
        public bool IsUsed;
    }

    public static class VmaProt
    {
        public const uint Read  = 1 << 0;
        public const uint Write = 1 << 1;
        public const uint Exec  = 1 << 2;
    }

    public static class VmaType
    {
        public const uint Anonymous  = 0;
        public const uint FileBacked = 1;
        public const uint Cow        = 2;
    }

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

            ProcessControlBlock* pcb = GetOrCreateProcess(userPml4Phys);
            if (pcb != null)
            {
                ulong binaryEnd = entryVirt + ((payloadSize + 4095) & ~0xFFFUL);
                AddVma(pcb, entryVirt, binaryEnd > entryVirt ? binaryEnd : entryVirt + 4096, VmaProt.Read | VmaProt.Write | VmaProt.Exec, VmaType.Anonymous);
                AddVma(pcb, userStackVirt, userStackVirt + userStackSize, VmaProt.Read | VmaProt.Write, VmaType.Anonymous);
            }

            return userPml4Phys;
        }

        private static bool IsManagedConventionalRam(ulong phys)
        {
            // Low IVT/BDA/EBDA + AP trampoline page are never valid MMIO.
            if (phys < 0x100000UL) return true;
            int count = Kernel.Boot.KernelHigh.UsableMemoryMap.RegionCount;
            if (count < 0) return false;
            if (count > Kernel.Boot.KernelHigh.BootMemoryMap.MaxRegions) count = Kernel.Boot.KernelHigh.BootMemoryMap.MaxRegions;
            for (int i = 0; i < count; i++)
            {
                ulong start = Kernel.Boot.KernelHigh.UsableMemoryMap.RegionStarts[i];
                ulong pages = Kernel.Boot.KernelHigh.UsableMemoryMap.RegionPageCounts[i];
                if (pages == 0) continue;
                if (pages > 0xFFFFFFFFFFFFFFFFUL / 4096UL) continue;
                ulong size = pages * 4096UL;
                if (size > 0xFFFFFFFFFFFFFFFFUL - start) continue;
                ulong end = start + size;
                if (phys >= start && phys < end) return true;
            }
            return false;
        }

        public static bool MapUserMmio(ulong userPml4Phys, ulong physAddr, ulong virtAddr, ulong sizeBytes, bool writeCombining)
        {
            const ulong MaxSingleMapping = 256UL * 1024 * 1024;
            if (sizeBytes == 0) return false;
            if (sizeBytes > MaxSingleMapping) return false;
            if (virtAddr == 0) return false;
            if (virtAddr >= Hhdm.Base) return false;
            if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - virtAddr) return false;
            if (virtAddr + sizeBytes > Hhdm.Base) return false;
            if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - physAddr) return false;

            if (userPml4Phys == 0)
            {
                userPml4Phys = Pml4PhysicalAddress;
            }
            if (userPml4Phys == 0) return false;

            ulong alignedPhys = physAddr & ~0xFFFUL;
            ulong alignedVirt = virtAddr & ~0xFFFUL;
            ulong pageOffset = physAddr & 0xFFFUL;
            if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - pageOffset - 0xFFFUL) return false;
            ulong alignedSize = (sizeBytes + pageOffset + 0xFFFUL) & ~0xFFFUL;
            if (alignedSize == 0) alignedSize = 4096;

            ulong flags = Paging.Present | Paging.Writable | Paging.User;
            if (writeCombining) flags |= Paging.Pat4K;
            else flags |= Paging.CacheDisable;

            if (alignedSize > MaxSingleMapping + 4096) return false;
            if (alignedSize > 0xFFFFFFFFFFFFFFFFUL - alignedVirt) return false;
            if (alignedVirt + alignedSize > Hhdm.Base) return false;

            // - ALLOW DMA-arena phys: AllocDma buffers are explicitly allocated
            //   for bus-mastering + cross-process sharing (shell surface <->
            //   display, NVMe queues, virtio rings). Denying them breaks IPC.
            // - ALLOW ACPI reclaim / initrd staging / GOP / ECAM-BAR MMIO:
            //   these live outside managed conventional RAM and roottask and
            //   drivers must map them to bootstrap.
            // - ALLOW fixed cluster transfer window (0x25000000UL) for filesystem IPC:
            //   allows fs.fat32 to stage cluster reads into demand-paged frames.
            // - DENY managed conventional RAM (PMM frames in UsableMemoryMap):
            //   prevents aliasing kernel heaps, page tables, thread stacks.
            // - DENY low memory (<1MiB): IVT/BDA/trampoline never valid MMIO.
            // LAPIC/IOAPIC aliasing is discouraged (see userland MmioMapper
            // advisory deny) but permitted here because input.hid performs
            // EOI via direct LAPIC mapping on this platform.
            if (alignedVirt == 0x25000000UL)
            {
                flags &= ~Paging.CacheDisable;
            }

            for (ulong chk = 0; chk < alignedSize; chk += 4096)
            {
                ulong pagePhys = alignedPhys + chk;
                if (pagePhys < 0x100000UL) return false;
                if (IsManagedConventionalRam(pagePhys))
                {
                    if (alignedVirt != 0x25000000UL) return false;
                }
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

            if (isCurrentCr3)
            {
                Cpu.WriteCr3(userPml4Phys);
            }
            return true;
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

        // Phase 1: Iteratively reclaim user half (PML4 0..255) of an address space.
        // Skips kernel half (256..511: HHDM + kernel text). Guards every free
        // with PageFrameAllocator.IsRam so MMIO (GOP framebuffer, PCI BARs mapped
        // via MapUserMmio) is never returned to the PMM bitmap.
        public static unsafe void DestroyAddressSpace(ulong pml4Phys)
        {
            if (pml4Phys == 0) return;
            ulong* pml4 = (ulong*)Hhdm.PhysicalToVirtual(pml4Phys & ~0xFFFUL);

            for (ulong pml4Idx = 0; pml4Idx < 256; pml4Idx++)
            {
                ulong pml4e = pml4[pml4Idx];
                if ((pml4e & Paging.Present) == 0) continue;

                ulong pdptPhys = pml4e & Paging.AddressMask;
                if (!PageFrameAllocator.IsRam(pdptPhys))
                {
                    pml4[pml4Idx] = 0;
                    continue;
                }
                ulong* pdpt = (ulong*)Hhdm.PhysicalToVirtual(pdptPhys);

                for (ulong pdptIdx = 0; pdptIdx < 512; pdptIdx++)
                {
                    ulong pdpte = pdpt[pdptIdx];
                    if ((pdpte & Paging.Present) == 0) continue;

                    if ((pdpte & Paging.LargePage) != 0)
                    {
                        // 1GB page: free 262144 contiguous 4K frames if managed RAM.
                        ulong base1G = pdpte & Paging.AddressMask;
                        if (PageFrameAllocator.IsRam(base1G))
                        {
                            PageFrameAllocator.FreeContiguousFrames(base1G, 262144);
                        }
                        pdpt[pdptIdx] = 0;
                        continue;
                    }

                    ulong pdPhys = pdpte & Paging.AddressMask;
                    if (!PageFrameAllocator.IsRam(pdPhys))
                    {
                        pdpt[pdptIdx] = 0;
                        continue;
                    }
                    ulong* pd = (ulong*)Hhdm.PhysicalToVirtual(pdPhys);

                    for (ulong pdIdx = 0; pdIdx < 512; pdIdx++)
                    {
                        ulong pde = pd[pdIdx];
                        if ((pde & Paging.Present) == 0) continue;

                        if ((pde & Paging.LargePage) != 0)
                        {
                            // 2MB page: free 512 contiguous frames if managed RAM.
                            ulong base2M = pde & Paging.AddressMask;
                            if (PageFrameAllocator.IsRam(base2M))
                            {
                                PageFrameAllocator.FreeContiguousFrames(base2M, 512);
                            }
                            pd[pdIdx] = 0;
                            continue;
                        }

                        ulong ptPhys = pde & Paging.AddressMask;
                        if (!PageFrameAllocator.IsRam(ptPhys))
                        {
                            pd[pdIdx] = 0;
                            continue;
                        }
                        ulong* pt = (ulong*)Hhdm.PhysicalToVirtual(ptPhys);

                        for (ulong ptIdx = 0; ptIdx < 512; ptIdx++)
                        {
                            ulong pte = pt[ptIdx];
                            if ((pte & Paging.Present) == 0) continue;
                            ulong framePhys = pte & Paging.AddressMask;
                            if (PageFrameAllocator.IsRam(framePhys))
                            {
                                PageFrameAllocator.FreeFrame(framePhys);
                            }
                            pt[ptIdx] = 0;
                        }

                        // Free the PT frame itself.
                        PageFrameAllocator.FreeFrame(ptPhys);
                        pd[pdIdx] = 0;
                    }

                    // Free the PD frame.
                    PageFrameAllocator.FreeFrame(pdPhys);
                    pdpt[pdptIdx] = 0;
                }

                // Free the PDPT frame.
                PageFrameAllocator.FreeFrame(pdptPhys);
                pml4[pml4Idx] = 0;
            }

            // Finally free the PML4 frame itself (caller must have switched CR3 away).
            if (PageFrameAllocator.IsRam(pml4Phys & ~0xFFFUL))
            {
                PageFrameAllocator.FreeFrame(pml4Phys & ~0xFFFUL);
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

        public static ulong* GetPtePointer(ulong pml4Phys, ulong virtAddr)
        {
            if (pml4Phys == 0)
            {
                pml4Phys = Pml4PhysicalAddress != 0 ? Pml4PhysicalAddress : Cpu.ReadCr3();
            }

            ulong pml4Idx = (virtAddr >> 39) & 0x1FF;
            ulong pdptIdx = (virtAddr >> 30) & 0x1FF;
            ulong pdIdx   = (virtAddr >> 21) & 0x1FF;
            ulong ptIdx   = (virtAddr >> 12) & 0x1FF;

            ulong* pml4 = (ulong*)(Hhdm.Base + (pml4Phys & ~0xFFFUL));
            if ((pml4[pml4Idx] & Paging.Present) == 0) return null;

            ulong* pdpt = (ulong*)(Hhdm.Base + (pml4[pml4Idx] & Paging.AddressMask));
            if ((pdpt[pdptIdx] & Paging.Present) == 0) return null;

            ulong* pd = (ulong*)(Hhdm.Base + (pdpt[pdptIdx] & Paging.AddressMask));
            if ((pd[pdIdx] & Paging.Present) == 0) return null;

            if ((pd[pdIdx] & Paging.LargePage) != 0) return null;

            ulong* pt = (ulong*)(Hhdm.Base + (pd[pdIdx] & Paging.AddressMask));
            return &pt[ptIdx];
        }

        public const int MaxVmas = 512;
        public const int MaxProcesses = 64;
        public const int VmaStorageBytes = 512 * 64;
        public const int ProcessStorageBytes = 64 * 128;

        [StructLayout(LayoutKind.Sequential, Size = VmaStorageBytes)]
        private struct VmaStorageBuffer { }

        [StructLayout(LayoutKind.Sequential, Size = ProcessStorageBytes)]
        private struct ProcessStorageBuffer { }

        private static VmaStorageBuffer s_vmaStorage;
        private static ProcessStorageBuffer s_processStorage;
        private static VirtualMemoryRegion* s_vmaPool;
        private static ProcessControlBlock* s_processPool;
        private static SpinLockWithIrqSave s_vmmLock;
        private static ulong s_nextProcessId = 1;

        public static void InitializeVmm()
        {
            fixed (VmaStorageBuffer* pVma = &s_vmaStorage)
            {
                s_vmaPool = (VirtualMemoryRegion*)pVma;
                for (int i = 0; i < MaxVmas; i++)
                {
                    s_vmaPool[i].IsUsed = false;
                    s_vmaPool[i].Next = null;
                }
            }

            fixed (ProcessStorageBuffer* pProc = &s_processStorage)
            {
                s_processPool = (ProcessControlBlock*)pProc;
                for (int i = 0; i < MaxProcesses; i++)
                {
                    s_processPool[i].IsUsed = false;
                    s_processPool[i].Next = null;
                    s_processPool[i].VmaHead = null;
                }
            }
        }

        public static ProcessControlBlock* GetOrCreateProcess(ulong pml4Phys)
        {
            if (pml4Phys == 0) return null;
            if (s_processPool == null) InitializeVmm();

            ulong rflags = s_vmmLock.Acquire();
            try
            {
                for (int i = 0; i < MaxProcesses; i++)
                {
                    if (s_processPool[i].IsUsed && s_processPool[i].PageDirectoryPhysBase == pml4Phys)
                    {
                        return &s_processPool[i];
                    }
                }

                for (int i = 0; i < MaxProcesses; i++)
                {
                    if (!s_processPool[i].IsUsed)
                    {
                        s_processPool[i].IsUsed = true;
                        s_processPool[i].Id = s_nextProcessId++;
                        s_processPool[i].PageDirectoryPhysBase = pml4Phys;
                        s_processPool[i].CSpaceRoot = null;
                        s_processPool[i].Next = null;
                        s_processPool[i].VmaHead = null;
                        s_processPool[i].HeapStart = 0x20000000UL;
                        s_processPool[i].HeapEnd = 0x20000000UL;
                        s_processPool[i].NextMmapAddress = 0x80000000UL;
                        return &s_processPool[i];
                    }
                }

                return null;
            }
            finally
            {
                s_vmmLock.Release(rflags);
            }
        }

        public static VirtualMemoryRegion* AllocateVma()
        {
            if (s_vmaPool == null) InitializeVmm();
            for (int i = 0; i < MaxVmas; i++)
            {
                if (!s_vmaPool[i].IsUsed)
                {
                    s_vmaPool[i].IsUsed = true;
                    s_vmaPool[i].Start = 0;
                    s_vmaPool[i].End = 0;
                    s_vmaPool[i].Protection = 0;
                    s_vmaPool[i].BackingType = 0;
                    s_vmaPool[i].FileHandle = 0;
                    s_vmaPool[i].FileOffset = 0;
                    s_vmaPool[i].Next = null;
                    return &s_vmaPool[i];
                }
            }
            return null;
        }

        public static VirtualMemoryRegion* AddVma(ProcessControlBlock* pcb, ulong start, ulong end, uint prot, uint backingType, uint fileHandle = 0, ulong fileOffset = 0)
        {
            if (pcb == null) return null;
            ulong rflags = s_vmmLock.Acquire();
            try
            {
                VirtualMemoryRegion* vma = AllocateVma();
                if (vma == null) return null;
                vma->Start = start;
                vma->End = end;
                vma->Protection = prot;
                vma->BackingType = backingType;
                vma->FileHandle = fileHandle;
                vma->FileOffset = fileOffset;
                vma->Next = pcb->VmaHead;
                pcb->VmaHead = vma;
                return vma;
            }
            finally
            {
                s_vmmLock.Release(rflags);
            }
        }

        public static VirtualMemoryRegion* FindVma(ProcessControlBlock* pcb, ulong address)
        {
            if (pcb == null) return null;
            ulong rflags = s_vmmLock.Acquire();
            try
            {
                VirtualMemoryRegion* curr = pcb->VmaHead;
                while (curr != null)
                {
                    if (address >= curr->Start && address < curr->End)
                    {
                        return curr;
                    }
                    curr = curr->Next;
                }
                return null;
            }
            finally
            {
                s_vmmLock.Release(rflags);
            }
        }

        public static ulong SysMmap(ulong pml4Phys, ulong addr, ulong length, uint prot, uint flags)
        {
            if (length == 0 || length > 256UL * 1024 * 1024) return ~0UL;
            ulong alignedLength = (length + 4095) & ~0xFFFUL;

            ProcessControlBlock* pcb = GetOrCreateProcess(pml4Phys);
            if (pcb == null) return ~0UL;

            if (addr == 0)
            {
                addr = pcb->NextMmapAddress;
                pcb->NextMmapAddress += alignedLength;
            }
            else
            {
                addr &= ~0xFFFUL;
            }

            if (addr >= Hhdm.Base || addr + alignedLength > Hhdm.Base) return ~0UL;

            if (prot == 0) prot = VmaProt.Read | VmaProt.Write;

            AddVma(pcb, addr, addr + alignedLength, prot, VmaType.Anonymous);
            return addr;
        }

        public static ulong SysBrk(ulong pml4Phys, ulong newBrk)
        {
            ProcessControlBlock* pcb = GetOrCreateProcess(pml4Phys);
            if (pcb == null) return 0;

            if (newBrk == 0 || newBrk == pcb->HeapEnd)
            {
                return pcb->HeapEnd;
            }

            if (newBrk < pcb->HeapStart)
            {
                return pcb->HeapEnd;
            }

            if (newBrk > pcb->HeapEnd)
            {
                ulong alignedEnd = (newBrk + 4095) & ~0xFFFUL;
                VirtualMemoryRegion* heapVma = FindVma(pcb, pcb->HeapStart);
                if (heapVma != null)
                {
                    heapVma->End = alignedEnd;
                }
                else
                {
                    AddVma(pcb, pcb->HeapStart, alignedEnd, VmaProt.Read | VmaProt.Write, VmaType.Anonymous);
                }
                pcb->HeapEnd = newBrk;
            }

            return pcb->HeapEnd;
        }

        public static bool ResolvePageFault(ulong faultAddress, ulong errorCode, InterruptContext* ctx)
        {
            // Only user addresses (< Hhdm.Base)
            if (faultAddress >= Hhdm.Base) return false;

            bool present = (errorCode & (1UL << 0)) != 0;
            bool isWrite = (errorCode & (1UL << 1)) != 0;
            bool isRsvd  = (errorCode & (1UL << 3)) != 0;
            if (isRsvd) return false;

            if (faultAddress >= Hhdm.Base) return false;

            ulong pml4Phys = (Scheduler.CurrentThread != null && Scheduler.CurrentThread->Pml4Address != 0)
                ? Scheduler.CurrentThread->Pml4Address
                : Cpu.ReadCr3();

            // 1. Check COW Write Fault
            ulong* pPte = GetPtePointer(pml4Phys, faultAddress);
            if (pPte != null && (*pPte & Paging.Present) != 0 && (*pPte & Paging.Cow) != 0 && isWrite)
            {
                ulong oldPhys = *pPte & Paging.AddressMask;
                ushort refCount = PhysicalFrameRefcount.Get(oldPhys);

                if (refCount <= 1)
                {
                    *pPte &= ~Paging.Cow;
                    *pPte |= Paging.Writable;
                    InvalidatePage(faultAddress);
                    return true;
                }
                else
                {
                    // Step 1: Allocate new frame
                    ulong newPhys = PageFrameAllocator.AllocateFrame();
                    if (newPhys == 0) return false;

                    // Step 2: Copy 4K block (no locks held)
                    Unsafe.CopyBlock((void*)Hhdm.PhysicalToVirtual(newPhys), (void*)Hhdm.PhysicalToVirtual(oldPhys), 4096);

                    // Step 3: Decrement old refcount
                    PhysicalFrameRefcount.Decrement(oldPhys);

                    // Step 4: Set new frame refcount
                    PhysicalFrameRefcount.Set(newPhys, 1);

                    // Step 5: Update PTE
                    ulong newFlags = (*pPte & ~Paging.AddressMask & ~Paging.Cow) | Paging.Writable;
                    *pPte = (newPhys & Paging.AddressMask) | newFlags;

                    // Step 6: Broadcast TLB shootdown
                    SmpTlbShootdown.BroadcastShootdown(pml4Phys, faultAddress & ~0xFFFUL, 1);

                    Kernel.Diagnostics.EarlySerial.WriteLine("[PASS] COW: Frame duplicated on write");
                    return true;
                }
            }

            // 2. Uncommitted Demand Paging Fault
            if (!present)
            {
                ProcessControlBlock* pcb = GetOrCreateProcess(pml4Phys);
                if (pcb == null) return false;

                VirtualMemoryRegion* vma = FindVma(pcb, faultAddress);
                if (vma == null) return false;

                if (isWrite && (vma->Protection & VmaProt.Write) == 0) return false;

                // 2a. Handle FILE_BACKED VMA
                if (vma->BackingType == VmaType.FileBacked)
                {
                    ulong pageOffsetInVma = (faultAddress & ~0xFFFUL) - vma->Start;
                    ulong fileOffset = vma->FileOffset + pageOffsetInVma;
                    uint clusterIndex = (uint)(fileOffset / 4096UL);

                    Kernel.Diagnostics.EarlySerial.Write("[PAGE_FAULT] Demand paging IPC to FAT32 for cluster ");
                    Kernel.Diagnostics.EarlySerial.WriteDec((long)clusterIndex);
                    Kernel.Diagnostics.EarlySerial.WriteLine("...");

                    ulong framePhys = PageFrameAllocator.AllocateFrame();
                    if (framePhys == 0) return false;

                    void* frameVirt = (void*)Hhdm.PhysicalToVirtual(framePhys);
                    Unsafe.InitBlock(frameVirt, 0, 4096);

                    // CRITICAL FIX 1: Enable interrupts before IPC so NVMe IRQs and APIC timer fire!
                    Cpu.EnableInterrupts();

                    if (Kernel.Boot.KernelHigh.FsEndpoint != null)
                    {
                        Kernel.Ipc.FastPathTransfer.Send(
                            Kernel.Boot.KernelHigh.FsEndpoint,
                            msgInfo: 5, // ReadCluster
                            d0: (ulong)vma->FileHandle,
                            d1: (ulong)clusterIndex,
                            d2: framePhys,
                            d3: 0,
                            badge: 0xBBBB,
                            isCall: true);
                    }

                    ulong* userPml4 = (ulong*)Hhdm.PhysicalToVirtual(pml4Phys);
                    ulong pageBase = faultAddress & ~0xFFFUL;
                    PhysicalFrameRefcount.Set(framePhys, 1);

                    ulong flags = Paging.Present | Paging.User;
                    if ((vma->Protection & VmaProt.Write) != 0)
                    {
                        flags |= Paging.Writable;
                    }
                    MapUserPage4K(userPml4, pageBase, framePhys, flags);
                    InvalidatePage(faultAddress);

                    Kernel.Diagnostics.EarlySerial.Write("[PASS] VMM: Demand paging resolved fault at ");
                    Kernel.Diagnostics.EarlySerial.WriteHex(faultAddress);
                    Kernel.Diagnostics.EarlySerial.WriteLine("");
                    return true;
                }

                // 2b. Handle Anonymous VMA
                ulong anonFramePhys = PageFrameAllocator.AllocateFrame();
                if (anonFramePhys == 0) return false;

                void* anonFrameVirt = (void*)Hhdm.PhysicalToVirtual(anonFramePhys);
                Unsafe.InitBlock(anonFrameVirt, 0, 4096);

                ulong* anonUserPml4 = (ulong*)Hhdm.PhysicalToVirtual(pml4Phys);
                ulong anonPageBase = faultAddress & ~0xFFFUL;

                if (!isWrite)
                {
                    // Read fault on anonymous VMA: map as COW with refcount=2 so write fault triggers COW
                    PhysicalFrameRefcount.Set(anonFramePhys, 2);
                    MapUserPage4K(anonUserPml4, anonPageBase, anonFramePhys, Paging.Present | Paging.User | Paging.Cow);
                }
                else
                {
                    // Direct write fault: map as Writable
                    PhysicalFrameRefcount.Set(anonFramePhys, 1);
                    MapUserPage4K(anonUserPml4, anonPageBase, anonFramePhys, Paging.Present | Paging.User | Paging.Writable);
                }

                InvalidatePage(faultAddress);

                Kernel.Diagnostics.EarlySerial.Write("[PASS] VMM: Demand paging resolved fault at ");
                Kernel.Diagnostics.EarlySerial.WriteHex(faultAddress);
                Kernel.Diagnostics.EarlySerial.WriteLine("");
                return true;
            }

            return false;
        }
    }
}
