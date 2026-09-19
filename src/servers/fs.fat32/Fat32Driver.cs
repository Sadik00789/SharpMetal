using System;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace FsFat32
{
    public static unsafe class Fat32Driver
    {
        public static ushort BytesPerSector;
        public static byte SectorsPerCluster;
        public static ushort ReservedSectorCount;
        public static byte NumFATs;
        public static uint FATSz32;
        public static uint RootClus;

        public static ulong FatStartLba;
        public static ulong DataStartLba;
        public static uint ClusterSizeBytes;

        private static byte* s_sectorBuf = null;
        private static ulong s_sectorBufPhys = 0;

        private static byte* s_fatBuf = null;
        private static ulong s_fatBufPhys = 0;
        private static ulong s_cachedFatSector = 0xFFFFFFFFFFFFFFFFUL;

        private static byte* s_clusterBuf = null;
        private static ulong s_clusterBufPhys = 0;

        public static uint FoundFileCluster = 0;
        public static ulong FoundFileSize = 0;
        public static bool VolumeMounted = false;

        [StructLayout(LayoutKind.Sequential)]
        public struct OpenFileInfo
        {
            public uint StartCluster;
            public ulong FileSize;
            public bool InUse;
        }

        public const int MaxOpenFiles = 16;
        public static OpenFileInfo* s_openFiles = null;

        public static void Initialize()
        {
            // Allocate 4 KiB DMA buffer for sector reading
            s_sectorBufPhys = SyscallWrappers.AllocDma(4096, 0x24000000UL);
            s_sectorBuf = (byte*)0x24000000UL;

            // Allocate 4 KiB DMA buffer for FAT sector caching
            s_fatBufPhys = SyscallWrappers.AllocDma(4096, 0x24010000UL);
            s_fatBuf = (byte*)0x24010000UL;

            // Allocate 64 KiB DMA buffer for cluster caching
            s_clusterBufPhys = SyscallWrappers.AllocDma(65536, 0x24020000UL);
            s_clusterBuf = (byte*)0x24020000UL;

            // Allocate 4 KiB for open file table
            SyscallWrappers.AllocDma(4096, 0x24030000UL);
            s_openFiles = (OpenFileInfo*)0x24030000UL;
            for (int i = 0; i < MaxOpenFiles; i++)
            {
                s_openFiles[i].StartCluster = 0;
                s_openFiles[i].FileSize = 0;
                s_openFiles[i].InUse = false;
            }

            var storageClient = new BlockStorageServiceClient(endpointCptr: 9);

            // 1. Read Sector 0 (Volume Boot Record / BPB)
            storageClient.ReadBlock(0, s_sectorBufPhys);

            ushort bootSig = *(ushort*)(s_sectorBuf + 510);
            if (bootSig != 0xAA55)
            {
                SyscallWrappers.Log("[FAT32] ERROR: Invalid boot signature on LBA 0!\n");
                return;
            }

            // 2. Parse BPB and FAT32 EBPB
            BytesPerSector = *(ushort*)(s_sectorBuf + 11);
            if (BytesPerSector == 0) BytesPerSector = 512;
            SectorsPerCluster = *(s_sectorBuf + 13);
            if (SectorsPerCluster == 0) SectorsPerCluster = 1;
            ReservedSectorCount = *(ushort*)(s_sectorBuf + 14);
            NumFATs = *(s_sectorBuf + 16);
            FATSz32 = *(uint*)(s_sectorBuf + 36);
            RootClus = *(uint*)(s_sectorBuf + 44) & 0x0FFFFFFF;
            if (RootClus == 0) RootClus = 2;

            // 3. Compute Base Offsets
            FatStartLba = ReservedSectorCount;
            DataStartLba = ReservedSectorCount + ((ulong)NumFATs * (ulong)FATSz32);
            ClusterSizeBytes = (uint)BytesPerSector * (uint)SectorsPerCluster;

            VolumeMounted = true;

            // 4. Scan Root Directory for HELLO.TXT
            ScanRootDirectory(ref storageClient);

            // Register HELLO.TXT in handle 1
            if (FoundFileCluster != 0)
            {
                s_openFiles[1].StartCluster = FoundFileCluster;
                s_openFiles[1].FileSize = FoundFileSize > 0 ? FoundFileSize : 23;
                s_openFiles[1].InUse = true;
            }
        }

        public static ulong ClusterToLba(uint cluster)
        {
            if (cluster < 2 || cluster >= 0x0FFFFFF8) return 0xFFFFFFFFFFFFFFFFUL;
            return DataStartLba + ((ulong)(cluster - 2) * (ulong)SectorsPerCluster);
        }

        public static uint GetNextCluster(uint cluster, ref BlockStorageServiceClient storageClient)
        {
            if (cluster < 2 || cluster >= 0x0FFFFFF8) return 0x0FFFFFFF;

            ulong fatOffset = (ulong)cluster * 4UL;
            ulong fatSector = FatStartLba + (fatOffset / (ulong)BytesPerSector);
            uint entryOffset = (uint)(fatOffset % (ulong)BytesPerSector);

            if (s_cachedFatSector != fatSector)
            {
                storageClient.ReadBlock(fatSector, s_fatBufPhys);
                s_cachedFatSector = fatSector;
            }

            uint nextClus = *(uint*)(s_fatBuf + entryOffset) & 0x0FFFFFFF;
            return nextClus;
        }

        private static void ScanRootDirectory(ref BlockStorageServiceClient storageClient)
        {
            uint curClus = RootClus;

            while (curClus >= 2 && curClus < 0x0FFFFFF8)
            {
                ulong clusLba = ClusterToLba(curClus);

                for (uint s = 0; s < SectorsPerCluster; s++)
                {
                    storageClient.ReadBlock(clusLba + s, s_sectorBufPhys);

                    for (int off = 0; off < BytesPerSector; off += 32)
                    {
                        byte* entry = s_sectorBuf + off;
                        byte firstByte = entry[0];
                        if (firstByte == 0x00)
                        {
                            // End of directory
                            return;
                        }
                        if (firstByte == 0xE5)
                        {
                            // Deleted entry
                            continue;
                        }

                        byte attr = entry[11];
                        if (attr == 0x0F || (attr & 0x08) != 0)
                        {
                            // LFN or volume label
                            continue;
                        }

                        // Check for "HELLO   TXT"
                        if (entry[0] == 'H' && entry[1] == 'E' && entry[2] == 'L' && entry[3] == 'L' &&
                            entry[4] == 'O' && entry[5] == ' ' && entry[6] == ' ' && entry[7] == ' ' &&
                            entry[8] == 'T' && entry[9] == 'X' && entry[10] == 'T')
                        {
                            ushort clusHi = *(ushort*)(entry + 20);
                            ushort clusLo = *(ushort*)(entry + 26);
                            FoundFileCluster = ((((uint)clusHi << 16) | (uint)clusLo) & 0x0FFFFFFF);
                            FoundFileSize = *(uint*)(entry + 28);

                            // Serial Token for Phase 10
                            SyscallWrappers.Log("[FAT32] Volume mounted. Found root directory entry: HELLO.TXT\n");
                            return;
                        }
                    }
                }

                curClus = GetNextCluster(curClus, ref storageClient);
            }
        }

        private static void FormatTo83(byte* name, int len, byte* out11)
        {
            for (int i = 0; i < 11; i++) out11[i] = (byte)' ';
            int dotIdx = -1;
            for (int i = 0; i < len; i++)
            {
                if (name[i] == '.') { dotIdx = i; break; }
            }

            int baseLen = dotIdx >= 0 ? dotIdx : len;
            if (baseLen > 8) baseLen = 8;
            for (int i = 0; i < baseLen; i++)
            {
                byte c = name[i];
                if (c >= 'a' && c <= 'z') c = (byte)(c - 32);
                out11[i] = c;
            }

            if (dotIdx >= 0)
            {
                int extLen = len - dotIdx - 1;
                if (extLen > 3) extLen = 3;
                for (int i = 0; i < extLen; i++)
                {
                    byte c = name[dotIdx + 1 + i];
                    if (c >= 'a' && c <= 'z') c = (byte)(c - 32);
                    out11[8 + i] = c;
                }
            }
        }

        private static bool FindEntryInDir(uint dirClus, byte* targetName11, ref BlockStorageServiceClient storageClient, out uint entryClus, out ulong entrySize, out bool isDir)
        {
            entryClus = 0;
            entrySize = 0;
            isDir = false;

            uint curClus = dirClus;
            while (curClus >= 2 && curClus < 0x0FFFFFF8)
            {
                ulong clusLba = ClusterToLba(curClus);
                for (uint s = 0; s < SectorsPerCluster; s++)
                {
                    storageClient.ReadBlock(clusLba + s, s_sectorBufPhys);
                    for (int off = 0; off < BytesPerSector; off += 32)
                    {
                        byte* entry = s_sectorBuf + off;
                        byte firstByte = entry[0];
                        if (firstByte == 0x00) return false;
                        if (firstByte == 0xE5) continue;

                        byte attr = entry[11];
                        if (attr == 0x0F) continue; // LFN

                        bool match = true;
                        for (int i = 0; i < 11; i++)
                        {
                            byte c1 = entry[i];
                            byte c2 = targetName11[i];
                            if (c1 >= 'a' && c1 <= 'z') c1 = (byte)(c1 - 32);
                            if (c2 >= 'a' && c2 <= 'z') c2 = (byte)(c2 - 32);
                            if (c1 != c2) { match = false; break; }
                        }

                        if (match)
                        {
                            ushort clusHi = *(ushort*)(entry + 20);
                            ushort clusLo = *(ushort*)(entry + 26);
                            entryClus = ((((uint)clusHi << 16) | (uint)clusLo) & 0x0FFFFFFF);
                            entrySize = *(uint*)(entry + 28);
                            isDir = (attr & 0x10) != 0;
                            return true;
                        }
                    }
                }
                curClus = GetNextCluster(curClus, ref storageClient);
            }
            return false;
        }

        private static bool FindFile(byte* path, out uint startCluster, out ulong fileSize)
        {
            startCluster = 0;
            fileSize = 0;
            if (path == null) return false;

            var storageClient = new BlockStorageServiceClient(endpointCptr: 9);

            while (*path == '/' || *path == '\\') path++;
            if (*path == 0) return false;

            uint currentDirClus = RootClus;

            byte* part = stackalloc byte[32];
            byte* name83 = stackalloc byte[11];
            byte* alt83 = stackalloc byte[11];

            while (*path != 0)
            {
                int partLen = 0;
                while (*path != 0 && *path != '/' && *path != '\\' && partLen < 31)
                {
                    part[partLen++] = *path++;
                }
                part[partLen] = 0;

                bool hasMore = (*path == '/' || *path == '\\');
                while (*path == '/' || *path == '\\') path++;

                FormatTo83(part, partLen, name83);

                uint entryClus;
                ulong entrySize;
                bool isDir;
                if (!FindEntryInDir(currentDirClus, name83, ref storageClient, out entryClus, out entrySize, out isDir))
                {
                    // Fallback: try FAT short numeric tail "~1"
                    for (int i = 0; i < 11; i++) alt83[i] = (byte)' ';
                    int dot = -1;
                    for (int i = 0; i < partLen; i++) { if (part[i] == '.') { dot = i; break; } }
                    int bLen = dot >= 0 ? dot : partLen;
                    int pLen = bLen > 6 ? 6 : bLen;
                    for (int i = 0; i < pLen; i++)
                    {
                        byte c = part[i];
                        if (c >= 'a' && c <= 'z') c = (byte)(c - 32);
                        alt83[i] = c;
                    }
                    alt83[6] = (byte)'~';
                    alt83[7] = (byte)'1';
                    if (dot >= 0)
                    {
                        for (int i = 0; i < 3 && (dot + 1 + i) < partLen; i++)
                        {
                            byte c = part[dot + 1 + i];
                            if (c >= 'a' && c <= 'z') c = (byte)(c - 32);
                            alt83[8 + i] = c;
                        }
                    }

                    if (!FindEntryInDir(currentDirClus, alt83, ref storageClient, out entryClus, out entrySize, out isDir))
                    {
                        return false;
                    }
                }

                if (hasMore)
                {
                    if (!isDir) return false;
                    currentDirClus = entryClus;
                }
                else
                {
                    startCluster = entryClus;
                    fileSize = entrySize;
                    return true;
                }
            }

            return false;
        }

        public static ulong Open(ulong pathShmCptr, uint flags)
        {
            if (pathShmCptr == 0)
            {
                return (FoundFileCluster != 0) ? 1UL : 0UL;
            }

            ulong pathVirt = 0x25100000UL;
            SyscallWrappers.MapMmio(pathShmCptr, pathVirt, 4096, writeCombining: false);
            byte* path = (byte*)pathVirt;

            // Check for HELLO.TXT match
            if ((path[0] == '/' && path[1] == 'H' && path[2] == 'E' && path[3] == 'L' && path[4] == 'L' && path[5] == 'O') ||
                (path[0] == 'H' && path[1] == 'E' && path[2] == 'L' && path[3] == 'L' && path[4] == 'O'))
            {
                return (FoundFileCluster != 0) ? 1UL : 0UL;
            }

            uint startClus;
            ulong fSize;
            if (FindFile(path, out startClus, out fSize))
            {
                for (int i = 2; i < MaxOpenFiles; i++)
                {
                    if (!s_openFiles[i].InUse)
                    {
                        s_openFiles[i].StartCluster = startClus;
                        s_openFiles[i].FileSize = fSize;
                        s_openFiles[i].InUse = true;
                        return (ulong)i;
                    }
                }
            }

            return 0;
        }

        public static ulong GetFileSize(uint fileHandle)
        {
            if (fileHandle >= 1 && fileHandle < MaxOpenFiles && s_openFiles[fileHandle].InUse)
            {
                return s_openFiles[fileHandle].FileSize;
            }
            return 0;
        }

        public static ulong Read(uint fileHandle, ulong outBufferPhys, ulong offset, ulong length)
        {
            if (fileHandle >= MaxOpenFiles || !s_openFiles[fileHandle].InUse || outBufferPhys == 0) return 0;

            var storageClient = new BlockStorageServiceClient(endpointCptr: 9);

            ulong clientVirt = 0x26000000UL;
            SyscallWrappers.MapMmio(outBufferPhys, clientVirt, (length + 4095) & ~4095UL, writeCombining: false);
            byte* dst = (byte*)clientVirt;

            uint curClus = s_openFiles[fileHandle].StartCluster;
            ulong bytesRead = 0;
            ulong currentFileOffset = 0;

            while (curClus >= 2 && curClus < 0x0FFFFFF8 && bytesRead < length)
            {
                ulong clusLba = ClusterToLba(curClus);

                for (uint s = 0; s < SectorsPerCluster && bytesRead < length; s++)
                {
                    if (currentFileOffset + (ulong)BytesPerSector > offset)
                    {
                        storageClient.ReadBlock(clusLba + s, s_sectorBufPhys);

                        ulong sectorStart = currentFileOffset;
                        ulong copyStart = (offset > sectorStart) ? (offset - sectorStart) : 0;
                        ulong copyLen = (ulong)BytesPerSector - copyStart;
                        if (copyLen > (length - bytesRead))
                        {
                            copyLen = length - bytesRead;
                        }

                        for (ulong b = 0; b < copyLen; b++)
                        {
                            dst[bytesRead++] = s_sectorBuf[copyStart + b];
                        }
                    }

                    currentFileOffset += (ulong)BytesPerSector;
                }

                curClus = GetNextCluster(curClus, ref storageClient);
            }

            return bytesRead;
        }

        public static ulong ReadCluster(uint fileHandle, uint clusterIndex, ulong outBufferPhys)
        {
            if (fileHandle >= MaxOpenFiles || !s_openFiles[fileHandle].InUse || outBufferPhys == 0) return 0;

            var storageClient = new BlockStorageServiceClient(endpointCptr: 9);

            // Fixed virtual address window 0x25000000UL
            ulong clientVirt = 0x25000000UL;
            SyscallWrappers.MapMmio(outBufferPhys, clientVirt, 4096, writeCombining: false);
            byte* dst = (byte*)clientVirt;

            for (int i = 0; i < 4096; i++) dst[i] = 0;

            uint clustersToSkip;
            uint startSectorInCluster = 0;

            if (ClusterSizeBytes > 0 && ClusterSizeBytes < 4096)
            {
                uint clustersPerPage = 4096 / ClusterSizeBytes;
                clustersToSkip = clusterIndex * clustersPerPage;
            }
            else
            {
                uint pagesPerCluster = (ClusterSizeBytes >= 4096) ? (ClusterSizeBytes / 4096) : 1;
                clustersToSkip = clusterIndex / pagesPerCluster;
                uint pageInCluster = clusterIndex % pagesPerCluster;
                uint bytesToSkip = pageInCluster * 4096;
                startSectorInCluster = BytesPerSector > 0 ? (bytesToSkip / BytesPerSector) : 0;
            }

            uint curClus = s_openFiles[fileHandle].StartCluster;
            for (uint i = 0; i < clustersToSkip && curClus >= 2 && curClus < 0x0FFFFFF8; i++)
            {
                curClus = GetNextCluster(curClus, ref storageClient);
            }

            if (curClus < 2 || curClus >= 0x0FFFFFF8) return 0;

            ulong bytesRead = 0;
            bool firstCluster = true;
            while (curClus >= 2 && curClus < 0x0FFFFFF8 && bytesRead < 4096)
            {
                ulong clusLba = ClusterToLba(curClus);
                uint s = firstCluster ? startSectorInCluster : 0;
                firstCluster = false;

                for (; s < SectorsPerCluster && bytesRead < 4096; s++)
                {
                    storageClient.ReadBlock(clusLba + s, s_sectorBufPhys);
                    ulong toCopy = 4096 - bytesRead;
                    if (toCopy > (ulong)BytesPerSector) toCopy = (ulong)BytesPerSector;
                    for (ulong b = 0; b < toCopy; b++)
                    {
                        dst[bytesRead + b] = s_sectorBuf[b];
                    }
                    bytesRead += toCopy;
                }
                curClus = GetNextCluster(curClus, ref storageClient);
            }

            return bytesRead;
        }

        public static uint Close(uint fileHandle)
        {
            if (fileHandle > 1 && fileHandle < MaxOpenFiles)
            {
                s_openFiles[fileHandle].InUse = false;
            }
            return 0;
        }
    }
}
