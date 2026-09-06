using System;
using System.Runtime.InteropServices;

namespace Kernel.Boot
{
    public enum EfiMemoryType : uint
    {
        EfiReservedMemoryType = 0,
        EfiLoaderCode = 1,
        EfiLoaderData = 2,
        EfiBootServicesCode = 3,
        EfiBootServicesData = 4,
        EfiRuntimeServicesCode = 5,
        EfiRuntimeServicesData = 6,
        EfiConventionalMemory = 7,
        EfiUnusableMemory = 8,
        EfiACPIReclaimMemory = 9,
        EfiACPIMemoryNVS = 10,
        EfiMemoryMappedIO = 11,
        EfiMemoryMappedIOPortSpace = 12,
        EfiPalCode = 13,
        EfiPersistentMemory = 14,
        EfiMaxMemoryType = 15
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EfiMemoryDescriptor
    {
        public uint Type;
        public uint Pad;
        public ulong PhysicalStart;
        public ulong VirtualStart;
        public ulong NumberOfPages;
        public ulong Attribute;
    }

    public static unsafe class UefiMemoryParser
    {
        public static ulong GetTotalUsableMemory(byte* mapBuffer, nuint mapSize, nuint descSize)
        {
            ulong totalBytes = 0;
            nuint count = mapSize / descSize;

            for (nuint i = 0; i < count; i++)
            {
                EfiMemoryDescriptor* desc = (EfiMemoryDescriptor*)(mapBuffer + (i * descSize));
                if (desc->Type == (uint)EfiMemoryType.EfiConventionalMemory)
                {
                    totalBytes += desc->NumberOfPages * 4096;
                }
            }

            return totalBytes;
        }

        public static ulong GetMaxPhysicalAddress(byte* mapBuffer, nuint mapSize, nuint descSize)
        {
            ulong maxAddr = 0;
            nuint count = mapSize / descSize;

            for (nuint i = 0; i < count; i++)
            {
                EfiMemoryDescriptor* desc = (EfiMemoryDescriptor*)(mapBuffer + (i * descSize));
                if (desc->Type == (uint)EfiMemoryType.EfiConventionalMemory ||
                    desc->Type == (uint)EfiMemoryType.EfiBootServicesCode ||
                    desc->Type == (uint)EfiMemoryType.EfiBootServicesData ||
                    desc->Type == (uint)EfiMemoryType.EfiLoaderCode ||
                    desc->Type == (uint)EfiMemoryType.EfiLoaderData)
                {
                    ulong end = desc->PhysicalStart + (desc->NumberOfPages * 4096);
                    if (end > maxAddr)
                    {
                        maxAddr = end;
                    }
                }
            }

            if (maxAddr == 0 || maxAddr > 0x4_0000_0000UL)
            {
                maxAddr = 0x4_0000_0000UL;
            }

            return maxAddr;
        }

        public static bool FindLargestConventionalRegion(byte* mapBuffer, nuint mapSize, nuint descSize, out ulong physStart, out ulong pageCount)
        {
            physStart = 0;
            pageCount = 0;
            nuint count = mapSize / descSize;

            for (nuint i = 0; i < count; i++)
            {
                EfiMemoryDescriptor* desc = (EfiMemoryDescriptor*)(mapBuffer + (i * descSize));
                if (desc->Type == (uint)EfiMemoryType.EfiConventionalMemory)
                {
                    if (desc->NumberOfPages > pageCount)
                    {
                        pageCount = desc->NumberOfPages;
                        physStart = desc->PhysicalStart;
                    }
                }
            }

            return pageCount > 0;
        }

        public static bool FindLargestConventionalRegionBelow4G(byte* mapBuffer, nuint mapSize, nuint descSize, out ulong physStart, out ulong pageCount)
        {
            physStart = 0;
            pageCount = 0;
            nuint count = mapSize / descSize;

            for (nuint i = 0; i < count; i++)
            {
                EfiMemoryDescriptor* desc = (EfiMemoryDescriptor*)(mapBuffer + (i * descSize));
                if (desc->Type == (uint)EfiMemoryType.EfiConventionalMemory)
                {
                    if (desc->PhysicalStart < 0x1_0000_0000UL)
                    {
                        ulong end = desc->PhysicalStart + (desc->NumberOfPages * 4096);
                        ulong usablePages = desc->NumberOfPages;
                        if (end > 0x1_0000_0000UL)
                        {
                            usablePages = (0x1_0000_0000UL - desc->PhysicalStart) / 4096;
                        }

                        if (usablePages > pageCount)
                        {
                            pageCount = usablePages;
                            physStart = desc->PhysicalStart;
                        }
                    }
                }
            }

            return pageCount > 0;
        }
    }
}
