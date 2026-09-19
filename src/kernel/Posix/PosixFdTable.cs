using System;
using System.Runtime.InteropServices;
using Kernel.Arch.x86_64.Hardware;
using Kernel.Concurrency;
using Kernel.Scheduling;

namespace Kernel.Posix
{
    public static class PosixFdType
    {
        public const byte None = 0;
        public const byte Stdin = 1;
        public const byte Stdout = 2;
        public const byte Stderr = 3;
        public const byte Vfs = 4;
        public const byte Socket = 5;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PosixFdEntry
    {
        public bool InUse;
        public byte Type;
        public ushort Flags;
        public uint ServerHandle;
        public ulong CurrentOffset;
    }

    public static unsafe class PosixFdTable
    {
        public const int MaxFds = 32;
        public const int MaxProcesses = 64;

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessFdRecord
        {
            public ProcessControlBlock* Pcb;
            public bool IsUsed;
            public fixed byte Pad[7];
            // 32 entries * 16 bytes = 512 bytes
            public fixed byte EntriesStorage[MaxFds * 16];
        }

        private const int TotalStorageBytes = MaxProcesses * 528;

        [StructLayout(LayoutKind.Sequential, Size = TotalStorageBytes)]
        private struct StorageBuffer { }

        private static StorageBuffer s_storage;
        private static ProcessFdRecord* s_tables;
        private static SpinLockWithIrqSave s_lock;
        private static bool s_initialized;

        public static void Initialize()
        {
            if (s_initialized) return;

            fixed (StorageBuffer* p = &s_storage)
            {
                s_tables = (ProcessFdRecord*)p;
                for (int i = 0; i < MaxProcesses; i++)
                {
                    s_tables[i].Pcb = null;
                    s_tables[i].IsUsed = false;
                }
            }
            s_initialized = true;
        }

        private static PosixFdEntry* GetEntryPtr(ProcessFdRecord* record, int fd)
        {
            if (fd < 0 || fd >= MaxFds) return null;
            return (PosixFdEntry*)(record->EntriesStorage + (fd * sizeof(PosixFdEntry)));
        }

        public static ProcessFdRecord* GetOrCreateTable(ProcessControlBlock* pcb)
        {
            if (pcb == null) return null;
            if (!s_initialized) Initialize();

            ulong rflags = s_lock.Acquire();
            try
            {
                for (int i = 0; i < MaxProcesses; i++)
                {
                    if (s_tables[i].IsUsed && s_tables[i].Pcb == pcb)
                    {
                        return &s_tables[i];
                    }
                }

                // Allocate new process FD table
                for (int i = 0; i < MaxProcesses; i++)
                {
                    if (!s_tables[i].IsUsed)
                    {
                        s_tables[i].IsUsed = true;
                        s_tables[i].Pcb = pcb;

                        // Clear all 32 entries
                        for (int fd = 0; fd < MaxFds; fd++)
                        {
                            PosixFdEntry* e = GetEntryPtr(&s_tables[i], fd);
                            e->InUse = false;
                            e->Type = PosixFdType.None;
                            e->Flags = 0;
                            e->ServerHandle = 0;
                            e->CurrentOffset = 0;
                        }

                        // Pre-populate standard file descriptors 0, 1, 2
                        PosixFdEntry* stdinEntry = GetEntryPtr(&s_tables[i], 0);
                        stdinEntry->InUse = true;
                        stdinEntry->Type = PosixFdType.Stdin;

                        PosixFdEntry* stdoutEntry = GetEntryPtr(&s_tables[i], 1);
                        stdoutEntry->InUse = true;
                        stdoutEntry->Type = PosixFdType.Stdout;

                        PosixFdEntry* stderrEntry = GetEntryPtr(&s_tables[i], 2);
                        stderrEntry->InUse = true;
                        stderrEntry->Type = PosixFdType.Stderr;

                        return &s_tables[i];
                    }
                }

                return null;
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static int AllocateFd(ProcessControlBlock* pcb, byte type, uint serverHandle, ushort flags)
        {
            ProcessFdRecord* rec = GetOrCreateTable(pcb);
            if (rec == null) return -1;

            ulong rflags = s_lock.Acquire();
            try
            {
                for (int fd = 3; fd < MaxFds; fd++)
                {
                    PosixFdEntry* e = GetEntryPtr(rec, fd);
                    if (!e->InUse)
                    {
                        e->InUse = true;
                        e->Type = type;
                        e->ServerHandle = serverHandle;
                        e->Flags = flags;
                        e->CurrentOffset = 0;
                        return fd;
                    }
                }
                return -1;
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static bool GetFd(ProcessControlBlock* pcb, int fd, out PosixFdEntry entry)
        {
            entry = default;
            if (fd < 0 || fd >= MaxFds) return false;

            ProcessFdRecord* rec = GetOrCreateTable(pcb);
            if (rec == null) return false;

            ulong rflags = s_lock.Acquire();
            try
            {
                PosixFdEntry* e = GetEntryPtr(rec, fd);
                if (e != null && e->InUse)
                {
                    entry = *e;
                    return true;
                }
                return false;
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static bool UpdateOffset(ProcessControlBlock* pcb, int fd, ulong newOffset)
        {
            if (fd < 0 || fd >= MaxFds) return false;

            ProcessFdRecord* rec = GetOrCreateTable(pcb);
            if (rec == null) return false;

            ulong rflags = s_lock.Acquire();
            try
            {
                PosixFdEntry* e = GetEntryPtr(rec, fd);
                if (e != null && e->InUse)
                {
                    e->CurrentOffset = newOffset;
                    return true;
                }
                return false;
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }

        public static bool FreeFd(ProcessControlBlock* pcb, int fd)
        {
            if (fd < 0 || fd >= MaxFds) return false;

            ProcessFdRecord* rec = GetOrCreateTable(pcb);
            if (rec == null) return false;

            ulong rflags = s_lock.Acquire();
            try
            {
                PosixFdEntry* e = GetEntryPtr(rec, fd);
                if (e != null && e->InUse)
                {
                    e->InUse = false;
                    e->Type = PosixFdType.None;
                    e->ServerHandle = 0;
                    e->Flags = 0;
                    e->CurrentOffset = 0;
                    return true;
                }
                return false;
            }
            finally
            {
                s_lock.Release(rflags);
            }
        }
    }
}
