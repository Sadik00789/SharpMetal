using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Boot;
using Microkernel.Abstractions.Services;
using PciServer.Pci;
using Userland.Runtime.ZeroAlloc.Interop;

namespace PciServer
{
    public struct PciServiceImpl : IPciService
    {
        public ulong FindDevice(uint vendorId, uint deviceId)
        {
            return PciEcamScanner.FindDevice(vendorId, deviceId);
        }

        public ulong GetBar(uint bus, uint dev, uint func, uint barIndex)
        {
            return PciEcamScanner.GetBar(bus, dev, func, barIndex);
        }

        public uint TriggerFlr(uint bus, uint dev, uint func)
        {
            return PciEcamScanner.TriggerFlr(bus, dev, func);
        }
    }

    public static unsafe class Program
    {
        public static PciServiceImpl s_serviceImpl;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "PciServerMain")]
        public static void Main()
        {
            KernelBootInfo bootInfo = default;
            SyscallWrappers.GetBootInfo(&bootInfo);

            // Map ECAM at 0x20000000 (covers 4 MB for buses 0..3)
            ulong ecamVirt = 0x20000000UL;
            SyscallWrappers.MapMmio(0xE0000000UL, ecamVirt, 4194304, writeCombining: false);
            PciEcamScanner.Initialize(ecamVirt);

            // Scan PCIe topology
            PciEcamScanner.ScanTopology();

            // Run RPC dispatcher on Slot 6
            s_serviceImpl = new PciServiceImpl();
            bool running = true;
            PciServiceDispatcher.Run(ref s_serviceImpl, endpointCptr: 6, ref running);

            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
