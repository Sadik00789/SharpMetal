using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Elf;
using Microkernel.Posix;
using Microkernel.Vfs;
using Userland.Runtime.ZeroAlloc.Interop;

namespace PosixRunner
{
    public static unsafe class Program
    {
        [DllImport("*")]
        public static extern void JumpToPie(ulong entryRip, ulong userRsp);

        private static void Log(string msg)
        {
            if (msg == null) return;
            byte* single = stackalloc byte[2];
            fixed (char* p = msg)
            {
                for (int i = 0; i < msg.Length; i++)
                {
                    byte b = (byte)p[i];
                    single[0] = b;
                    single[1] = 0;
                    SyscallWrappers.Log(single);
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "PosixRunnerMain")]
        public static void Main()
        {
            // 1. Determine target binary path from parameter page at 0x3F000000
            byte* argPage = (byte*)0x3F000000UL;
            string targetPath = "/bin/posix_test";

            if (argPage[0] != 0)
            {
                int len = 0;
                while (len < 255 && argPage[len] != 0) len++;
                if (len > 0)
                {
                    // Check known test targets
                    if (Matches(argPage, len, "/bin/posix_test")) targetPath = "/bin/posix_test";
                    else if (Matches(argPage, len, "/bin/hello.pie")) targetPath = "/bin/hello.pie";
                    else if (Matches(argPage, len, "/bin/cat.pie")) targetPath = "/bin/cat.pie";
                    else if (Matches(argPage, len, "posix_test")) targetPath = "/bin/posix_test";
                    else if (Matches(argPage, len, "hello.pie")) targetPath = "/bin/hello.pie";
                    else if (Matches(argPage, len, "cat.pie")) targetPath = "/bin/cat.pie";
                }
            }

            Log("[POSIX_RUNNER] Hosting PIE binary: ");
            Log(targetPath);
            Log("\n");

            // 2. Open target PIE binary via VFS IPC
            ulong fileHandle = 0;
            for (int retry = 0; retry < 50; retry++)
            {
                fileHandle = VfsClient.Open(targetPath, 0);
                if (fileHandle != 0 && fileHandle < 100) break;

                // Alternate path fallback
                if (targetPath == "/bin/posix_test")
                {
                    fileHandle = VfsClient.Open("/bin/hello.pie", 0);
                    if (fileHandle != 0 && fileHandle < 100) break;
                    fileHandle = VfsClient.Open("/bin/posix.pie", 0);
                    if (fileHandle != 0 && fileHandle < 100) break;
                }
                SyscallWrappers.Yield();
            }

            if (fileHandle == 0 || fileHandle >= 100)
            {
                Log("[POSIX_RUNNER] ERROR: Failed to open binary via VFS IPC!\n");
                SyscallWrappers.Exit(1);
                return;
            }

            ulong fileSize = VfsClient.GetFileSize((uint)fileHandle);
            if (fileSize == 0 || fileSize > 16UL * 1024 * 1024)
            {
                Log("[POSIX_RUNNER] ERROR: Invalid file size from VFS: 0x");
                PrintHex(fileSize);
                Log("\n");
                VfsClient.Close((uint)fileHandle);
                SyscallWrappers.Exit(1);
                return;
            }

            // 3. Allocate staging DMA buffer to read entire ELF binary
            const ulong ElfStagingVirt = 0x38000000UL;
            ulong allocSize = (fileSize + 4095) & ~4095UL;
            ulong stagingPhys = SyscallWrappers.AllocDma(allocSize, ElfStagingVirt);
            if (stagingPhys == 0)
            {
                Log("[POSIX_RUNNER] ERROR: Failed to allocate staging DMA buffer!\n");
                VfsClient.Close((uint)fileHandle);
                SyscallWrappers.Exit(1);
                return;
            }

            ulong bytesRead = VfsClient.Read((uint)fileHandle, stagingPhys, 0, fileSize);
            VfsClient.Close((uint)fileHandle);

            if (bytesRead < (ulong)sizeof(Elf64_Ehdr))
            {
                Log("[POSIX_RUNNER] ERROR: Read less than ELF header size from VFS!\n");
                SyscallWrappers.Exit(1);
                return;
            }

            // 4. Validate ELF64 static PIE header
            Elf64_Ehdr* ehdr = (Elf64_Ehdr*)ElfStagingVirt;
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
                Log("[POSIX_RUNNER] ERROR: Invalid ELF64/PIE header!\n");
                SyscallWrappers.Exit(1);
                return;
            }

            const ulong AslrBase = 0x0000000040000000UL;
            Elf64_Phdr* phdrs = (Elf64_Phdr*)((byte*)ehdr + ehdr->e_phoff);
            Elf64_Phdr* dynPhdr = null;

            // 5. Load and map each PT_LOAD segment
            for (ushort i = 0; i < ehdr->e_phnum; i++)
            {
                if (phdrs[i].p_type == ElfConstants.PT_LOAD)
                {
                    ulong segVirt = AslrBase + phdrs[i].p_vaddr;
                    ulong segMemSz = phdrs[i].p_memsz;
                    ulong segFileSz = phdrs[i].p_filesz;
                    ulong segFileOff = phdrs[i].p_offset;

                    ulong alignedStart = segVirt & ~0xFFFUL;
                    ulong alignedEnd = (segVirt + segMemSz + 4095) & ~0xFFFUL;
                    ulong segAllocBytes = alignedEnd - alignedStart;

                    SyscallWrappers.AllocDma(segAllocBytes, alignedStart);

                    byte* src = (byte*)ElfStagingVirt + segFileOff;
                    byte* dst = (byte*)segVirt;

                    for (ulong b = 0; b < segFileSz; b++)
                    {
                        dst[b] = src[b];
                    }
                    for (ulong b = segFileSz; b < segMemSz; b++)
                    {
                        dst[b] = 0;
                    }
                }
                else if (phdrs[i].p_type == ElfConstants.PT_DYNAMIC)
                {
                    dynPhdr = &phdrs[i];
                }
            }

            // 6. Apply R_X86_64_RELATIVE Relocations
            if (dynPhdr != null)
            {
                Elf64_Dyn* dyn = (Elf64_Dyn*)(AslrBase + dynPhdr->p_vaddr);
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
                    ulong count = relaSz / (relaEnt != 0 ? relaEnt : 24);
                    Elf64_Rela* rela = (Elf64_Rela*)(AslrBase + relaVaddr);
                    for (ulong r = 0; r < count; r++)
                    {
                        if (rela[r].R_Type == ElfConstants.R_X86_64_RELATIVE)
                        {
                            ulong* target = (ulong*)(AslrBase + rela[r].r_offset);
                            *target = AslrBase + (ulong)rela[r].r_addend;
                        }
                    }
                }
            }

            // 7. Map 64KB user stack at 0x00007FFFFFF00000
            const ulong UserStackBase = 0x00007FFFFFF00000UL;
            const ulong UserStackSize = 65536;
            const ulong DefaultStackTop = UserStackBase + UserStackSize;
            SyscallWrappers.AllocDma(UserStackSize, UserStackBase);

            // 8. Build System V AMD64 initial stack layout (argc, argv, envp, auxv)
            ulong userRsp = PosixStackBuilder.BuildInitialStack(
                UserStackBase,
                DefaultStackTop,
                targetPath,
                AslrBase + ehdr->e_entry,
                AslrBase + ehdr->e_phoff,
                (ulong)ehdr->e_phnum,
                (ulong)sizeof(Elf64_Phdr));

            ulong entryRip = AslrBase + ehdr->e_entry;

            Log("[POSIX_RUNNER] Jumping to PIE entry point at 0x");
            PrintHex(entryRip);
            Log("...\n");

            // 9. Jump to PIE: switches RSP, executes SysSetAbi(1), clears GPRs, jumps to entryRip
            JumpToPie(entryRip, userRsp);
        }

        private static bool Matches(byte* buf, int len, string s)
        {
            if (len != s.Length) return false;
            for (int i = 0; i < len; i++)
            {
                if (buf[i] != (byte)s[i]) return false;
            }
            return true;
        }

        private static void PrintHex(ulong val)
        {
            byte* hexDigits = stackalloc byte[16];
            for (int i = 0; i < 10; i++) hexDigits[i] = (byte)('0' + i);
            for (int i = 0; i < 6; i++) hexDigits[10 + i] = (byte)('A' + i);

            byte* buf = stackalloc byte[17];
            int idx = 0;
            for (int shift = 60; shift >= 0; shift -= 4)
            {
                byte nibble = (byte)((val >> shift) & 0xF);
                if (idx > 0 || nibble > 0 || shift == 0)
                {
                    buf[idx++] = hexDigits[nibble];
                }
            }
            buf[idx] = 0;
            SyscallWrappers.Log(buf);
        }
    }
}
