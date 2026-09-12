using System;
using System.Runtime.CompilerServices;

namespace PciServer.Acpi
{
    /// <summary>
    /// Userland MMIO request validator for unprivileged drivers.
    /// Denies any mapping whose physical range overlaps registered system RAM
    /// (kernel text, DMA arena, PMM frames, initrd, GOP) so a compromised
    /// driver cannot alias arbitrary kernel / system RAM as MMIO.
    /// Fail-closed: zero-size, wrapping, null, or RAM-overlapping requests
    /// are rejected. Non-RAM MMIO windows (ECAM, BARs, APIC-adjacent device
    /// memory) must either be explicitly registered or fall inside the
    /// canonical MMIO hole; everything else is denied when strict mode is on.
    /// </summary>
    public static unsafe class MmioMapper
    {
        public const int MaxRegions = 16;

        // Well-known x86-64 MMIO hole. Physical RAM on QEMU/UEFI lives below
        // this; ECAM (0xE0000000), HPET, NVMe BARs (0xFEBxxxxx), LAPIC
        // (0xFEE00000) and IOAPIC (0xFEC00000) live at/above it.
        public const ulong MmioHoleBase = 0xC0000000UL;
        public const ulong MaxPhysAllow = 0x1_0000_0000_00UL; // 1 TB cap
        public const ulong MaxSingleMapping = 256UL * 1024 * 1024; // 256 MB

        // Privileged local-APIC / IOAPIC pages must never be mapped by
        // unprivileged drivers (interrupt injection / IPI forgery).
        private const ulong LapicBase = 0xFEE00000UL;
        private const ulong LapicSize = 0x1000UL;
        private const ulong IoApicBase = 0xFEC00000UL;
        private const ulong IoApicSize = 0x1000UL;

        private struct RegionStorage
        {
            public fixed ulong RamBases[16];
            public fixed ulong RamSizes[16];
            public fixed ulong MmioBases[16];
            public fixed ulong MmioSizes[16];
        }

        private static RegionStorage s_storage;
        private static int s_ramCount;
        private static int s_mmioCount;
        private static bool s_initialized;

        public static int RamRegionCount => s_ramCount;
        public static int MmioWindowCount => s_mmioCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Overlaps(ulong baseA, ulong sizeA, ulong baseB, ulong sizeB)
        {
            if (sizeA == 0 || sizeB == 0) return false;
            if (sizeA > 0xFFFFFFFFFFFFFFFFUL - baseA) return true; // treat wrap as overlap (fail-closed)
            if (sizeB > 0xFFFFFFFFFFFFFFFFUL - baseB) return true;
            ulong endA = baseA + sizeA;
            ulong endB = baseB + sizeB;
            return baseA < endB && baseB < endA;
        }

        /// <summary>
        /// One-time init: installs a conservative default RAM deny-list
        /// ([0, MmioHoleBase)) so that even if the caller never registers the
        /// UEFI memory map, low RAM can never be aliased as MMIO.
        /// </summary>
        public static void Initialize()
        {
            if (s_initialized) return;
            s_ramCount = 0;
            s_mmioCount = 0;
            // Default deny: everything below the MMIO hole is treated as RAM.
            // Explicit RegisterSystemRam / RegisterMmioWindow calls refine this.
            fixed (ulong* bases = s_storage.RamBases)
            fixed (ulong* sizes = s_storage.RamSizes)
            {
                bases[0] = 0;
                sizes[0] = MmioHoleBase;
            }
            s_ramCount = 1;
            s_initialized = true;
        }

        /// <summary>
        /// Register a physical RAM range that must never be mappable as MMIO.
        /// Returns false on bad input or table exhaustion.
        /// </summary>
        public static bool RegisterSystemRam(ulong basePhys, ulong sizeBytes)
        {
            if (!s_initialized) Initialize();
            if (sizeBytes == 0) return false;
            if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - basePhys) return false;
            if (s_ramCount >= MaxRegions) return false;
            fixed (ulong* bases = s_storage.RamBases)
            fixed (ulong* sizes = s_storage.RamSizes)
            {
                bases[s_ramCount] = basePhys;
                sizes[s_ramCount] = sizeBytes;
            }
            s_ramCount++;
            return true;
        }

        /// <summary>
        /// Register an allowed MMIO window (ECAM, PCI BAR, device memory).
        /// Returns false on bad input or table exhaustion.
        /// </summary>
        public static bool RegisterMmioWindow(ulong basePhys, ulong sizeBytes)
        {
            if (!s_initialized) Initialize();
            if (sizeBytes == 0) return false;
            if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - basePhys) return false;
            if (s_mmioCount >= MaxRegions) return false;
            fixed (ulong* bases = s_storage.MmioBases)
            fixed (ulong* sizes = s_storage.MmioSizes)
            {
                bases[s_mmioCount] = basePhys;
                sizes[s_mmioCount] = sizeBytes;
            }
            s_mmioCount++;
            return true;
        }

        /// <summary>True iff [phys, phys+size) overlaps any registered RAM.</summary>
        public static bool IsSystemRam(ulong phys, ulong sizeBytes)
        {
            if (!s_initialized) Initialize();
            if (sizeBytes == 0) return true; // fail-closed
            if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - phys) return true;
            fixed (ulong* bases = s_storage.RamBases)
            fixed (ulong* sizes = s_storage.RamSizes)
            {
                for (int i = 0; i < s_ramCount; i++)
                {
                    if (Overlaps(phys, sizeBytes, bases[i], sizes[i])) return true;
                }
            }
            return false;
        }

        /// <summary>True iff [phys, phys+size) lies wholly inside one registered MMIO window.</summary>
        public static bool IsRegisteredMmioWindow(ulong phys, ulong sizeBytes)
        {
            if (!s_initialized) Initialize();
            if (sizeBytes == 0) return false;
            if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - phys) return false;
            ulong end = phys + sizeBytes;
            fixed (ulong* bases = s_storage.MmioBases)
            fixed (ulong* sizes = s_storage.MmioSizes)
            {
                for (int i = 0; i < s_mmioCount; i++)
                {
                    if (sizes[i] > 0xFFFFFFFFFFFFFFFFUL - bases[i]) continue;
                    ulong wEnd = bases[i] + sizes[i];
                    if (phys >= bases[i] && end <= wEnd) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Strict validator for userland driver MMIO map requests.
        /// Denies: zero size, oversized (>256MB), wrapping ranges, null page,
        /// LAPIC/IOAPIC pages, any RAM overlap, and anything above the
        /// physical cap. Non-RAM ranges inside the MMIO hole (or inside an
        /// explicitly registered window, or above 4GiB for 64-bit BARs) pass.
        /// </summary>
        public static bool ValidateMmioRequest(ulong physAddr, ulong sizeBytes)
        {
            if (!s_initialized) Initialize();
            if (sizeBytes == 0) return false;
            if (sizeBytes > MaxSingleMapping) return false;
            if (physAddr == 0 && sizeBytes > 0)
            {
                // Null-page alias is never a legitimate MMIO window.
                // (ECAM/BARs never live at physical 0.)
                return false;
            }
            // Wrap check.
            if (sizeBytes > 0xFFFFFFFFFFFFFFFFUL - physAddr) return false;
            ulong end = physAddr + sizeBytes;
            if (end > MaxPhysAllow) return false;

            // Privileged interrupt-controller pages are never mappable.
            if (Overlaps(physAddr, sizeBytes, LapicBase, LapicSize)) return false;
            if (Overlaps(physAddr, sizeBytes, IoApicBase, IoApicSize)) return false;

            // System RAM can never be aliased as MMIO.
            if (IsSystemRam(physAddr, sizeBytes)) return false;

            // Explicitly registered windows always pass (post RAM check).
            if (IsRegisteredMmioWindow(physAddr, sizeBytes)) return true;

            // Canonical MMIO hole passes (ECAM, HPET, PCI hole devices).
            if (physAddr >= MmioHoleBase && end <= 0x1_0000_0000UL) return true;

            // 64-bit BARs above 4GiB pass (non-RAM device memory).
            if (physAddr >= 0x1_0000_0000UL && end <= MaxPhysAllow) return true;

            // Anything else (e.g. unregistered low-memory hole punch) is denied.
            return false;
        }
    }
}
