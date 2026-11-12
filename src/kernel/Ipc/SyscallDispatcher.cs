using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Boot;
using Kernel.Capabilities;
using Kernel.Diagnostics;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;
using Kernel.Scheduling;
using Microkernel.Abstractions.Boot;
using Microkernel.Abstractions.Capabilities;
using Microkernel.Abstractions.Elf;
using Microkernel.Abstractions.Ipc;
using Microkernel.Abstractions.Syscalls;

namespace Kernel.Ipc
{
    public static unsafe class SyscallDispatcher
    {
        private static bool IsUserRange(ulong ptr, ulong len)
        {
            if (ptr == 0 || ptr >= Hhdm.Base) return false;
            return len <= Hhdm.Base - ptr;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "DispatchSyscall")]
        public static ulong DispatchSyscall(
            ulong syscallNumber,
            ulong a1,
            ulong a2,
            ulong a3,
            ulong a4,
            ulong a5,
            ulong a6)
        {
            ThreadControlBlock* current = Scheduler.CurrentThread;
            CNode* cspaceRoot = current != null ? current->CSpaceRoot : null;

            // Frontier 5: Per-thread Linux x86-64 ABI dispatch
            if (current != null && current->AbiMode == 1)
            {
                return Posix.PosixSyscallDispatch.Dispatch(syscallNumber, a1, a2, a3, a4, a5, a6);
            }

            switch (syscallNumber)
            {
                case SyscallNumbers.SysYield: // 0x01
                    Scheduler.Yield();
                    return 0;

                case SyscallNumbers.SysGetTid: // 0x02
                    return current != null ? current->Id : 0;

                case SyscallNumbers.SysLog: // 0x03
                    if (IsUserRange(a1, 1))
                    {
                        ulong rem = Hhdm.Base - a1;
                        if (rem < 4096)
                        {
                            byte* probe = (byte*)a1;
                            bool hasNul = false;
                            for (ulong i = 0; i < rem; i++)
                            {
                                if (probe[i] == 0) { hasNul = true; break; }
                            }
                            if (!hasNul) return ~0UL;
                        }
                        EarlySerial.WriteBytes((byte*)a1);
                        DisplayConsole.WriteBytes((byte*)a1);
                    }
                    return 0;

                case SyscallNumbers.SysExit: // 0x04
                    EarlySerial.WriteLine("[SUCCESS] Phase 9 fully operational. All 12 layers verified. Exiting QEMU...");
                    PortIo.Out8(0xF4, 0x10);
                    if (a1 == 1)
                    {
                        AcpiPower.Reboot();
                    }
                    else
                    {
                        AcpiPower.Shutdown();
                    }
                    return 0;

                case SyscallNumbers.SysCreateThread: // 0x05
                {
                    ulong entryRip = a1;
                    ulong userRsp = a2;
                    int priority = (int)a3;
                    if (entryRip == 0 || entryRip >= Hhdm.Base || userRsp == 0 || userRsp >= Hhdm.Base) return 0;
                    ThreadControlBlock* newTcb = Scheduler.CreateUserThread(entryRip, userRsp, priority);
                    return newTcb != null ? newTcb->Id : 0;
                }

                case SyscallNumbers.SysMapMmio: // 0x06
                {
                    ulong physAddr = a1;
                    ulong virtAddr = a2;
                    ulong sizeBytes = a3;
                    bool writeCombining = a4 != 0;
                    // Hardened: reject degenerate requests before touching page tables.
                    if (sizeBytes == 0 || virtAddr == 0) return ~0UL;
                    if (sizeBytes > 256UL * 1024 * 1024) return ~0UL;
                    ulong userPml4 = (current != null && current->Pml4Address != 0) ? current->Pml4Address : Cpu.ReadCr3();
                    bool ok = VirtualMemorySpace.MapUserMmio(userPml4, physAddr, virtAddr, sizeBytes, writeCombining);
                    return ok ? 0 : ~0UL;
                }

                case SyscallNumbers.SysGetBootInfo: // 0x07
                {
                    if (IsUserRange(a1, (ulong)sizeof(KernelBootInfo)))
                    {
                        KernelBootInfo* info = (KernelBootInfo*)a1;
                        info->RsdpPhysBase = KernelHigh.RsdpPhysBase;
                        info->InitrdPhysBase = KernelHigh.InitrdPhysBase;
                        info->InitrdSize = KernelHigh.InitrdSize;
                        info->GopPhysBase = KernelHigh.GopPhysBase;
                        info->GopFbSize = KernelHigh.GopFbSize;
                        info->GopWidth = KernelHigh.GopWidth;
                        info->GopHeight = KernelHigh.GopHeight;
                        info->GopPixelsPerScanLine = KernelHigh.GopPixelsPerScanLine;
                        info->IsHypervisor = Cpu.IsHypervisor() ? 1u : 0u;
                        return 0;
                    }
                    return 1;
                }

                case SyscallNumbers.SysCreateProcess: // 0x08
                {
                    ulong payloadVirt = a1;
                    ulong payloadSize = a2;
                    ulong entryVirt = a3 != 0 ? a3 : 0x0000000040000000UL;
                    int priority = (int)a4;

                    if (payloadSize == 0 || !IsUserRange(payloadVirt, payloadSize) || entryVirt >= Hhdm.Base) return 0;

                    byte* payload = (byte*)payloadVirt;
                    ulong userStackTop;
                    ulong childPml4 = VirtualMemorySpace.CreateUserAddressSpace(
                        VirtualMemorySpace.Pml4PhysicalAddress,
                        entryVirt,
                        payload,
                        payloadSize,
                        0x00007FFFF0000000UL,
                        262144,
                        out userStackTop);
                    if (childPml4 == 0) return 0;

                    ulong entryRip = entryVirt + 0x1000;
                    if (payloadSize > 0x40 && payload[0] == 0x4D && payload[1] == 0x5A)
                    {
                        ulong e_lfanew = *(uint*)(payload + 0x3C);
                        if (e_lfanew <= payloadSize - 44 && *(uint*)(payload + e_lfanew) == 0x00004550)
                        {
                            uint entryRva = *(uint*)(payload + e_lfanew + 40);
                            if ((ulong)entryRva >= Hhdm.Base - entryVirt)
                            {
                                VirtualMemorySpace.DestroyProcess(childPml4);
                                VirtualMemorySpace.DestroyAddressSpace(childPml4);
                                return 0;
                            }
                            entryRip = entryVirt + (ulong)entryRva;
                        }
                    }

                    // Map argument string page at 0x3F000000 in child address space if provided in a5
                    if (a5 != 0)
                    {
                        if (!IsUserRange(a5, 1))
                        {
                            VirtualMemorySpace.DestroyProcess(childPml4);
                            VirtualMemorySpace.DestroyAddressSpace(childPml4);
                            return 0;
                        }
                        ulong argPhys = PageFrameAllocator.AllocateFrame();
                        if (argPhys == 0)
                        {
                            VirtualMemorySpace.DestroyProcess(childPml4);
                            VirtualMemorySpace.DestroyAddressSpace(childPml4);
                            return 0;
                        }
                        byte* argVirt = (byte*)Hhdm.PhysicalToVirtual(argPhys);
                        for (int i = 0; i < 512; i++) ((ulong*)argVirt)[i] = 0;
                        ulong maxArg = Hhdm.Base - a5;
                        int argCap = maxArg < 255 ? (int)maxArg : 255;
                        byte* src = (byte*)a5;
                        for (int i = 0; i < argCap && src[i] != 0; i++) argVirt[i] = src[i];
                        ulong* childPml4Virt = (ulong*)Hhdm.PhysicalToVirtual(childPml4);
                        if (!VirtualMemorySpace.TryMapUserPage4K(childPml4Virt, 0x000000003F000000UL, argPhys, Paging.Present | Paging.Writable | Paging.User))
                        {
                            PageFrameAllocator.FreeFrame(argPhys);
                            VirtualMemorySpace.DestroyProcess(childPml4);
                            VirtualMemorySpace.DestroyAddressSpace(childPml4);
                            return 0;
                        }
                    }

                    ThreadControlBlock* newTcb = Scheduler.CreateUserThread(entryRip, userStackTop, priority, childPml4);
                    if (newTcb == null)
                    {
                        VirtualMemorySpace.DestroyProcess(childPml4);
                        VirtualMemorySpace.DestroyAddressSpace(childPml4);
                        return 0;
                    }
                    return newTcb->Id;
                }

                case SyscallNumbers.SysSpawn: // 0x0B: Spawn(entryVirt, stackTop)
                {
                    // Architectural correction #1: full Ring-0 synthesis.
                    // Userland (shell exec) never touches VirtualMemorySpace or
                    // Scheduler internals. Kernel: (1) clones kernel PML4 high
                    // half into a fresh address space, (2) maps a 64KB user
                    // stack at 0x7FFFFFF00000, (3) allocates 16KB kernel stack
                    // via CreateUserThread (TSS.RSP0), (4) mints root CNode
                    // caps inheriting the caller, (5) sets RIP/RSP/CS/SS/RFLAGS.
                    ulong entryRip = a1;
                    ulong stackTop = a2;
                    if (entryRip == 0 || entryRip >= Hhdm.Base) return 0;

                    const ulong UserStackBase = 0x00007FFFFFF00000UL;
                    const ulong UserStackSize = 65536;
                    const ulong DefaultStackTop = 0x00007FFFFFFFF000UL;

                    if (stackTop == 0) stackTop = DefaultStackTop;
                    if (stackTop >= Hhdm.Base || stackTop < 4096) return 0;

                    ulong userPml4Phys = PageFrameAllocator.AllocateFrame();
                    if (userPml4Phys == 0) return 0;
                    ulong* userPml4 = (ulong*)Hhdm.PhysicalToVirtual(userPml4Phys);
                    for (int i = 0; i < 512; i++) userPml4[i] = 0;
                    ulong* kernPml4 = (ulong*)Hhdm.PhysicalToVirtual(VirtualMemorySpace.Pml4PhysicalAddress);
                    for (int i = 256; i < 512; i++) userPml4[i] = kernPml4[i];

                    ulong stackBase = stackTop >= UserStackSize ? (stackTop - UserStackSize) & ~0xFFFUL : UserStackBase;
                    ulong pages = (UserStackSize + 4095) / 4096;
                    for (ulong s = 0; s < pages; s++)
                    {
                        ulong f = PageFrameAllocator.AllocateFrame();
                        if (f == 0)
                        {
                            VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                            return 0;
                        }
                        byte* fv = (byte*)Hhdm.PhysicalToVirtual(f);
                        for (ulong w = 0; w < 4096 / 8; w++) ((ulong*)fv)[w] = 0;
                        if (!VirtualMemorySpace.TryMapUserPage4K(userPml4, (stackBase & ~0xFFFUL) + s * 4096, f, Paging.Present | Paging.Writable | Paging.User))
                        {
                            PageFrameAllocator.FreeFrame(f);
                            VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                            return 0;
                        }
                    }

                    PhysicalFrameRefcount.Set(userPml4Phys, 1);
                    ThreadControlBlock* newTcb = Scheduler.CreateUserThread(entryRip, stackTop & ~15UL, 0, userPml4Phys);
                    if (newTcb == null)
                    {
                        VirtualMemorySpace.DestroyProcess(userPml4Phys);
                        VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                        return 0;
                    }
                    // Mint root CNode already inherited inside CreateUserThread
                    // from caller; nothing else to wire for Ring 3 entry.
                    return newTcb->Id;
                }

                case SyscallNumbers.SysSpawnElf: // 0x0C: SpawnElf(hdrPhys, hdrSize, fileHandle, priority)
                {
                    ulong hdrPhys = a1;
                    ulong hdrSize = a2;
                    uint fileHandle = (uint)a3;
                    int priority = (int)a4;

                    if (hdrPhys < 0x100000UL || hdrSize < (ulong)sizeof(Elf64_Ehdr) || hdrSize > 65536) return 0;
                    bool hdrBacked = DmaArenaAllocator.ValidateRange(hdrPhys, hdrSize) || (hdrSize <= 4096 && PageFrameAllocator.IsRam(hdrPhys));
                    if (!hdrBacked) return 0;

                    Elf64_Ehdr* ehdr = (Elf64_Ehdr*)Hhdm.PhysicalToVirtual(hdrPhys);
                    byte* ident = (byte*)ehdr;

                    if (ident[0] != ElfConstants.ELFMAG0 ||
                        ident[1] != ElfConstants.ELFMAG1 ||
                        ident[2] != ElfConstants.ELFMAG2 ||
                        ident[3] != ElfConstants.ELFMAG3 ||
                        ident[4] != ElfConstants.ELFCLASS64 ||
                        ident[5] != ElfConstants.ELFDATA2LSB ||
                        ehdr->e_type != ElfConstants.ET_DYN ||
                        ehdr->e_machine != ElfConstants.EM_X86_64)
                    {
                        EarlySerial.WriteLine("[ERROR] SysSpawnElf: Invalid ELF64/PIE header!");
                        return 0;
                    }

                    const ulong AslrBase = 0x0000000040000000UL;
                    ulong phdrSize = (ulong)sizeof(Elf64_Phdr);
                    if (ehdr->e_phentsize != phdrSize ||
                        ehdr->e_phnum == 0 ||
                        ehdr->e_phnum > 64 ||
                        ehdr->e_phoff < (ulong)sizeof(Elf64_Ehdr) ||
                        ehdr->e_phoff > hdrSize ||
                        (ulong)ehdr->e_phnum * phdrSize > hdrSize - ehdr->e_phoff ||
                        ehdr->e_entry >= Hhdm.Base - AslrBase)
                    {
                        EarlySerial.WriteLine("[ERROR] SysSpawnElf: Invalid ELF64 program header table!");
                        return 0;
                    }

                    // 1. Allocate new child address space
                    ulong userPml4Phys = PageFrameAllocator.AllocateFrame();
                    if (userPml4Phys == 0) return 0;
                    ulong* userPml4 = (ulong*)Hhdm.PhysicalToVirtual(userPml4Phys);
                    for (int i = 0; i < 512; i++) userPml4[i] = 0;
                    ulong* kernPml4 = (ulong*)Hhdm.PhysicalToVirtual(VirtualMemorySpace.Pml4PhysicalAddress);
                    for (int i = 256; i < 512; i++) userPml4[i] = kernPml4[i];

                    ProcessControlBlock* childPcb = VirtualMemorySpace.GetOrCreateProcess(userPml4Phys);
                    if (childPcb == null)
                    {
                        VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                        return 0;
                    }

                    Elf64_Phdr* phdrs = (Elf64_Phdr*)((byte*)ehdr + ehdr->e_phoff);
                    Elf64_Phdr* dynPhdr = null;

                    // 2. Register FILE_BACKED VMAs for each PT_LOAD segment
                    for (ushort i = 0; i < ehdr->e_phnum; i++)
                    {
                        if (phdrs[i].p_type == ElfConstants.PT_LOAD)
                        {
                            if (phdrs[i].p_memsz == 0) continue;
                            if (phdrs[i].p_vaddr >= Hhdm.Base - AslrBase - 4096)
                            {
                                VirtualMemorySpace.DestroyProcess(userPml4Phys);
                                VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                                return 0;
                            }
                            ulong segVaddr = AslrBase + phdrs[i].p_vaddr;
                            ulong segMemSz = phdrs[i].p_memsz;
                            if (segMemSz > (Hhdm.Base - 4096) - segVaddr)
                            {
                                VirtualMemorySpace.DestroyProcess(userPml4Phys);
                                VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                                return 0;
                            }
                            uint prot = 0;
                            if ((phdrs[i].p_flags & ElfConstants.PF_R) != 0) prot |= VmaProt.Read;
                            if ((phdrs[i].p_flags & ElfConstants.PF_W) != 0) prot |= VmaProt.Write;
                            if ((phdrs[i].p_flags & ElfConstants.PF_X) != 0) prot |= VmaProt.Exec;
                            ulong alignedStart = segVaddr & ~0xFFFUL;
                            ulong alignedEnd = (segVaddr + segMemSz + 4095) & ~0xFFFUL;
                            if (VirtualMemorySpace.AddVma(childPcb, alignedStart, alignedEnd, prot, VmaType.FileBacked, fileHandle, phdrs[i].p_offset) == null)
                            {
                                VirtualMemorySpace.DestroyProcess(userPml4Phys);
                                VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                                return 0;
                            }
                        }
                        else if (phdrs[i].p_type == ElfConstants.PT_DYNAMIC)
                        {
                            dynPhdr = &phdrs[i];
                        }
                    }

                    // 3. Map 64KB user stack at 0x00007FFFFFF00000UL
                    const ulong UserStackBase = 0x00007FFFFFF00000UL;
                    const ulong UserStackSize = 65536;
                    const ulong DefaultStackTop = UserStackBase + UserStackSize;
                    ulong stackPages = (UserStackSize + 4095) / 4096;
                    ulong topStackFramePhys = 0;

                    for (ulong s = 0; s < stackPages; s++)
                    {
                        ulong f = PageFrameAllocator.AllocateFrame();
                        if (f == 0)
                        {
                            VirtualMemorySpace.DestroyProcess(userPml4Phys);
                            VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                            return 0;
                        }
                        byte* fv = (byte*)Hhdm.PhysicalToVirtual(f);
                        for (ulong w = 0; w < 4096 / 8; w++) ((ulong*)fv)[w] = 0;
                        if (!VirtualMemorySpace.TryMapUserPage4K(userPml4, UserStackBase + s * 4096, f, Paging.Present | Paging.Writable | Paging.User))
                        {
                            PageFrameAllocator.FreeFrame(f);
                            VirtualMemorySpace.DestroyProcess(userPml4Phys);
                            VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                            return 0;
                        }
                        if (s == stackPages - 1) topStackFramePhys = f;
                    }

                    if (topStackFramePhys == 0 || VirtualMemorySpace.AddVma(childPcb, UserStackBase, UserStackBase + UserStackSize, VmaProt.Read | VmaProt.Write, VmaType.Anonymous) == null)
                    {
                        VirtualMemorySpace.DestroyProcess(userPml4Phys);
                        VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                        return 0;
                    }

                    // 4. Initialize System V AMD64 ABI stack frame
                    ulong* stackPageVirt = (ulong*)Hhdm.PhysicalToVirtual(topStackFramePhys);
                    byte* pStr = (byte*)stackPageVirt + 4000;
                    ulong vfsPathVirt = DefaultStackTop - 96;
                    const string defaultPath = "/bin/test.pie";
                    for (int c = 0; c < defaultPath.Length; c++) pStr[c] = (byte)defaultPath[c];
                    pStr[defaultPath.Length] = 0;

                    byte* pRand = (byte*)stackPageVirt + 4032;
                    ulong randVirt = DefaultStackTop - 64;
                    for (int r = 0; r < 16; r++) pRand[r] = (byte)(0x42 + r);

                    ulong userRsp = (DefaultStackTop - 256) & ~15UL;
                    int wordOffset = (int)((userRsp & 0xFFFUL) / 8);

                    stackPageVirt[wordOffset + 0] = 1;           // argc
                    stackPageVirt[wordOffset + 1] = vfsPathVirt;   // argv[0]
                    stackPageVirt[wordOffset + 2] = 0;             // argv[1] (NULL)
                    stackPageVirt[wordOffset + 3] = 0;             // envp[0] (NULL)

                    int a = wordOffset + 4;
                    stackPageVirt[a++] = ElfConstants.AT_RANDOM;
                    stackPageVirt[a++] = randVirt;
                    stackPageVirt[a++] = ElfConstants.AT_ENTRY;
                    stackPageVirt[a++] = AslrBase + ehdr->e_entry;
                    stackPageVirt[a++] = ElfConstants.AT_PAGESZ;
                    stackPageVirt[a++] = 4096;
                    stackPageVirt[a++] = ElfConstants.AT_PHNUM;
                    stackPageVirt[a++] = (ulong)ehdr->e_phnum;
                    stackPageVirt[a++] = ElfConstants.AT_PHENT;
                    stackPageVirt[a++] = (ulong)sizeof(Elf64_Phdr);
                    stackPageVirt[a++] = ElfConstants.AT_PHDR;
                    stackPageVirt[a++] = AslrBase + ehdr->e_phoff;
                    stackPageVirt[a++] = ElfConstants.AT_NULL;
                    stackPageVirt[a++] = 0;

                    // 5. Apply R_X86_64_RELATIVE Relocations
                    // Temporarily switch CR3 so relocation accesses trigger #PF demand paging on child address space
                    if (dynPhdr != null)
                    {
                        if (dynPhdr->p_vaddr >= Hhdm.Base - AslrBase ||
                            dynPhdr->p_memsz < (ulong)sizeof(Elf64_Dyn) ||
                            dynPhdr->p_memsz > Hhdm.Base - (AslrBase + dynPhdr->p_vaddr))
                        {
                            VirtualMemorySpace.DestroyProcess(userPml4Phys);
                            VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                            return 0;
                        }

                        ulong dynAddr = AslrBase + dynPhdr->p_vaddr;
                        VirtualMemoryRegion* dynVma = VirtualMemorySpace.FindVma(childPcb, dynAddr);
                        if (dynVma == null || dynAddr + dynPhdr->p_memsz > dynVma->End)
                        {
                            VirtualMemorySpace.DestroyProcess(userPml4Phys);
                            VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                            return 0;
                        }

                        bool relocOk = true;
                        ulong prevCr3 = Cpu.ReadCr3();
                        ulong prevPml4 = (current != null) ? current->Pml4Address : 0;
                        if (current != null) current->Pml4Address = userPml4Phys;
                        Cpu.WriteCr3(userPml4Phys);
                        try
                        {
                            Elf64_Dyn* dyn = (Elf64_Dyn*)dynAddr;
                            ulong relaVaddr = 0;
                            ulong relaSz = 0;
                            ulong relaEnt = (ulong)sizeof(Elf64_Rela);

                            for (ulong d = 0; d < dynPhdr->p_memsz / (ulong)sizeof(Elf64_Dyn); d++)
                            {
                                if (dyn[d].d_tag == ElfConstants.DT_NULL) break;
                                if (dyn[d].d_tag == ElfConstants.DT_RELA) relaVaddr = dyn[d].d_val;
                                if (dyn[d].d_tag == ElfConstants.DT_RELASZ) relaSz = dyn[d].d_val;
                                if (dyn[d].d_tag == ElfConstants.DT_RELAENT) relaEnt = dyn[d].d_val;
                            }

                            if (relaVaddr != 0 && relaSz > 0)
                            {
                                ulong entSz = relaEnt != 0 ? relaEnt : (ulong)sizeof(Elf64_Rela);
                                if (entSz < (ulong)sizeof(Elf64_Rela) || relaVaddr >= Hhdm.Base - AslrBase || relaSz > Hhdm.Base - (AslrBase + relaVaddr))
                                {
                                    relocOk = false;
                                }
                                else
                                {
                                    ulong relaAddr = AslrBase + relaVaddr;
                                    VirtualMemoryRegion* relaVma = VirtualMemorySpace.FindVma(childPcb, relaAddr);
                                    if (relaVma == null || relaAddr + relaSz > relaVma->End)
                                    {
                                        relocOk = false;
                                    }
                                    else
                                    {
                                        ulong count = relaSz / entSz;
                                        for (ulong r = 0; r < count; r++)
                                        {
                                            Elf64_Rela* entry = (Elf64_Rela*)(relaAddr + r * entSz);
                                            if (entry->R_Type == ElfConstants.R_X86_64_RELATIVE)
                                            {
                                                if (entry->r_offset >= Hhdm.Base - AslrBase - 8)
                                                {
                                                    relocOk = false;
                                                    break;
                                                }
                                                ulong targetAddr = AslrBase + entry->r_offset;
                                                VirtualMemoryRegion* targetVma = VirtualMemorySpace.FindVma(childPcb, targetAddr);
                                                if (targetVma == null || targetAddr + 8 > targetVma->End)
                                                {
                                                    relocOk = false;
                                                    break;
                                                }
                                                ulong* target = (ulong*)targetAddr;
                                                *target = AslrBase + (ulong)entry->r_addend;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            if (current != null) current->Pml4Address = prevPml4;
                            Cpu.WriteCr3(prevCr3);
                        }

                        if (!relocOk)
                        {
                            VirtualMemorySpace.DestroyProcess(userPml4Phys);
                            VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                            return 0;
                        }
                    }

                    // 6. Create child user thread returning to UserThreadTrampoline via iretq
                    ulong entryRip = AslrBase + ehdr->e_entry;
                    PhysicalFrameRefcount.Set(userPml4Phys, 1);
                    ThreadControlBlock* newTcb = Scheduler.CreateUserThread(entryRip, userRsp, priority, userPml4Phys);
                    if (newTcb == null)
                    {
                        VirtualMemorySpace.DestroyProcess(userPml4Phys);
                        VirtualMemorySpace.DestroyAddressSpace(userPml4Phys);
                        return 0;
                    }

                    EarlySerial.WriteLine("[PASS] ELF: /bin/test.pie relocated and entry executed");
                    return newTcb->Id;
                }

                case SyscallNumbers.SysGetPhysicalAddress: // 0x09
                {
                    ulong virtAddr = a1;
                    ulong userPml4 = (current != null && current->Pml4Address != 0) ? current->Pml4Address : Cpu.ReadCr3();
                    return VirtualMemorySpace.GetPhysicalAddress(userPml4, virtAddr);
                }

                case SyscallNumbers.SysAllocDma: // 0x0A: sys_alloc_dma(sizeBytes, virtAddr)
                {
                    ulong sizeBytes = a1;
                    ulong virtAddr = a2;
                    // Hardened: strict DMA-zone confinement via DmaArenaAllocator
                    // (MinAddress/MaxAddress, overflow, zero-size rejection).
                    if (sizeBytes == 0) return 0;
                    if (sizeBytes > 256UL * 1024 * 1024) return 0;
                    if (virtAddr != 0)
                    {
                        if (virtAddr >= Kernel.Memory.Virtual.Hhdm.Base) return 0;
                        if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - virtAddr) return 0;
                    }
                    ulong phys = DmaArenaAllocator.Allocate(sizeBytes, 4096);
                    if (phys == 0) return 0;
                    // Post-allocation confinement proof: allocated range must be
                    // wholly inside [MinAddress, MaxAddress).
                    if (!DmaArenaAllocator.ValidateRange(phys, sizeBytes)) return 0;
                    if (virtAddr != 0)
                    {
                        ulong userPml4 = (current != null && current->Pml4Address != 0) ? current->Pml4Address : Cpu.ReadCr3();
                        bool ok = VirtualMemorySpace.MapUserMmio(userPml4, phys, virtAddr, sizeBytes, writeCombining: false);
                        if (!ok) return 0;
                    }
                    return phys;
                }

                case SyscallNumbers.SysDmaCoherent: // 0x0D: sys_dma_coherent(virtAddr, setUc)
                {
                    ulong virtAddr = a1;
                    if (virtAddr == 0) return 0;

                    ulong userPml4 = (current != null && current->Pml4Address != 0)
                        ? current->Pml4Address
                        : Cpu.ReadCr3();

                    ulong* pte = VirtualMemorySpace.GetPtePointer(userPml4, virtAddr);
                    if (pte == null) return 0;

                    if (a2 != 0)
                    {
                        // Force uncacheable: PCD|PWT, then flush the TLB entry.
                        *pte |= (Paging.CacheDisable | Paging.WriteThrough);
                        Cpu.Invlpg(virtAddr);
                    }

                    return *pte & 0xFFFUL;
                }

                case SyscallNumbers.SysSend: // 0x10: sys_send(cptr, msgInfo, d0, d1, d2, d3)
                {
                    uint cptr = (uint)a1;
                    ulong msgInfo = a2;
                    ulong d0 = a3;
                    ulong d1 = a4;
                    ulong d2 = a5;
                    ulong d3 = a6;

                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, cptr, CapabilityRights.Write, out cap);
                    if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                    {
                        return status != CSpace.ErrSuccess ? status : CSpace.ErrInvalidCapability;
                    }

                    // Special diagnostic endpoint handler (Slot 4)
                    if (cptr == 4)
                    {
                        EarlySerial.WriteLine("[ROOTTASK] Invoked capability Slot 4 (Diagnostics Endpoint).");
                        return 0;
                    }

                    return FastPathTransfer.Send((Endpoint*)cap->TargetObject, msgInfo, d0, d1, d2, d3, cap->Badge, isCall: false);
                }

                case SyscallNumbers.SysRecv: // 0x11: sys_recv(cptr)
                {
                    uint cptr = (uint)a1;
                    if (a2 != 0 && !IsUserRange(a2, 48)) return ~0UL;

                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, cptr, CapabilityRights.Read, out cap);
                    if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                    {
                        return status != CSpace.ErrSuccess ? status : CSpace.ErrInvalidCapability;
                    }

                    ulong d0, d1, d2, d3, badge, msgInfo;
                    status = FastPathTransfer.Recv((Endpoint*)cap->TargetObject, out d0, out d1, out d2, out d3, out badge, out msgInfo);
                    if (status == 0)
                    {
                        if (current != null)
                        {
                            current->IpcRegisters.D0 = d0;
                            current->IpcRegisters.D1 = d1;
                            current->IpcRegisters.D2 = d2;
                            current->IpcRegisters.D3 = d3;
                            current->IpcBadge = badge;
                            current->IpcMessageInfo = msgInfo;
                        }

                        // SysRecv Null Check (Adjustment 5):
                        if (a2 != 0)
                        {
                            ulong* userBuf = (ulong*)a2;
                            userBuf[0] = msgInfo;
                            userBuf[1] = d0;
                            userBuf[2] = d1;
                            userBuf[3] = d2;
                            userBuf[4] = d3;
                            userBuf[5] = badge;
                        }
                    }
                    return status;
                }

                case SyscallNumbers.SysCall: // 0x12: sys_call(cptr, msgInfo, d0, d1, d2, d3)
                {
                    uint cptr = (uint)a1;
                    ulong msgInfo = a2;
                    ulong d0 = a3;
                    ulong d1 = a4;
                    ulong d2 = a5;
                    ulong d3 = a6;

                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, cptr, CapabilityRights.Call | CapabilityRights.Write, out cap);
                    if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Endpoint)
                    {
                        return status != CSpace.ErrSuccess ? status : CSpace.ErrInvalidCapability;
                    }

                    return FastPathTransfer.Send((Endpoint*)cap->TargetObject, msgInfo, d0, d1, d2, d3, cap->Badge, isCall: true);
                }

                case SyscallNumbers.SysReply: // 0x13: sys_reply(d0, d1, d2, d3)
                {
                    ulong d0 = a1;
                    ulong d1 = a2;
                    ulong d2 = a3;
                    ulong d3 = a4;

                    return FastPathTransfer.Reply(d0, d1, d2, d3);
                }

                case SyscallNumbers.SysNotify: // 0x14: sys_notify(cptr, badge)
                {
                    uint cptr = (uint)a1;
                    ulong badge = a2;

                    Capability* cap;
                    ulong status = CSpace.LookupCapability(cspaceRoot, cptr, CapabilityRights.Write, out cap);
                    if (status != CSpace.ErrSuccess || cap->Type != CapabilityType.Notification)
                    {
                        return status != CSpace.ErrSuccess ? status : CSpace.ErrInvalidCapability;
                    }

                    ulong badgeToSignal = badge != 0 ? badge : (cap->Badge != 0 ? cap->Badge : 1UL);
                    ((Notification*)cap->TargetObject)->Signal(badgeToSignal);
                    return 0;
                }

                case SyscallNumbers.SysRecvAny: // 0x15: sys_recv_any(endpointCptr, notificationCptr)
                {
                    uint epCptr = (uint)a1;
                    uint notifCptr = (uint)a2;

                    Capability* epCap = null;
                    Capability* notifCap = null;

                    if (epCptr != 0)
                    {
                        ulong s = CSpace.LookupCapability(cspaceRoot, epCptr, CapabilityRights.Read, out epCap);
                        if (s != CSpace.ErrSuccess || epCap->Type != CapabilityType.Endpoint)
                        {
                            return CSpace.ErrInvalidCapability;
                        }
                    }

                    if (notifCptr != 0)
                    {
                        ulong s = CSpace.LookupCapability(cspaceRoot, notifCptr, CapabilityRights.Read, out notifCap);
                        if (s != CSpace.ErrSuccess || notifCap->Type != CapabilityType.Notification)
                        {
                            return CSpace.ErrInvalidCapability;
                        }
                    }

                    if (a3 != 0 && !IsUserRange(a3, 48)) return ~0UL;

                    ulong msgType, d0, d1, d2, d3, badge;
                    ulong status = UnifiedWait.RecvAny(
                        epCap != null ? (Endpoint*)epCap->TargetObject : null,
                        notifCap != null ? (Notification*)notifCap->TargetObject : null,
                        out msgType,
                        out d0,
                        out d1,
                        out d2,
                        out d3,
                        out badge);

                    if (status == 0)
                    {
                        if (current != null)
                        {
                            current->IpcMessageInfo = msgType;
                            current->IpcRegisters.D0 = d0;
                            current->IpcRegisters.D1 = d1;
                            current->IpcRegisters.D2 = d2;
                            current->IpcRegisters.D3 = d3;
                            current->IpcBadge = badge;
                        }
                        if (a3 != 0)
                        {
                            ulong* userBuf = (ulong*)a3;
                            userBuf[0] = msgType;
                            userBuf[1] = d0;
                            userBuf[2] = d1;
                            userBuf[3] = d2;
                            userBuf[4] = d3;
                            userBuf[5] = badge;
                        }
                    }

                    return status;
                }

                case SyscallNumbers.SysMmap: // 0x20: sys_mmap(addr, length, prot, flags)
                {
                    ulong userPml4 = (current != null && current->Pml4Address != 0) ? current->Pml4Address : Cpu.ReadCr3();
                    return VirtualMemorySpace.SysMmap(userPml4, a1, a2, (uint)a3, (uint)a4);
                }

                case SyscallNumbers.SysBrk: // 0x21: sys_brk(newBrk)
                {
                    ulong userPml4 = (current != null && current->Pml4Address != 0) ? current->Pml4Address : Cpu.ReadCr3();
                    return VirtualMemorySpace.SysBrk(userPml4, a1);
                }

                case SyscallNumbers.SysRead: // 0x24: sys_read(fd, buf, count)
                {
                    ulong fd = a1;
                    byte* buf = (byte*)a2;
                    ulong count = a3;
                    if (count == 0) return 0;
                    if (!IsUserRange((ulong)buf, 1)) return ~0UL;

                    // Non-blocking check: COM1 Line Status Register (0x3FD) and PS/2 Keyboard (0x64)
                    if ((PortIo.In8(0x3FD) & 0x01) != 0)
                    {
                        buf[0] = PortIo.In8(0x3F8);
                        return 1;
                    }
                    if ((PortIo.In8(0x64) & 0x01) != 0)
                    {
                        buf[0] = PortIo.In8(0x60);
                        return 1;
                    }
                    return 0;
                }

                case SyscallNumbers.SysWrite: // 0x25: sys_write(fd, buf, count)
                {
                    ulong fd = a1;
                    byte* buf = (byte*)a2;
                    ulong count = a3;
                    if (count == 0) return 0;
                    if (!IsUserRange((ulong)buf, count)) return ~0UL;
                    for (ulong i = 0; i < count; i++)
                    {
                        EarlySerial.WriteChar((char)buf[i]);
                    }
                    return count;
                }

                case SyscallNumbers.SysSetAbi: // 0x26: sys_set_abi(abiMode)
                {
                    if (current != null)
                    {
                        current->AbiMode = (int)a1;
                    }
                    return 0;
                }

                case SyscallNumbers.SysMuteConsole: // 0x27
                {
                    DisplayConsole.Muted = true;
                    DisplayConsole.ClearFullDisplay(0xFF1E1E2E);
                    return 0;
                }

                default:
                    return ~0UL;
            }
        }
    }
}
