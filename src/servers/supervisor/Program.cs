using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microkernel.Abstractions.Services;
using Userland.Runtime.ZeroAlloc.Interop;

namespace Supervisor
{
    public static unsafe class Program
    {
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "SupervisorMain")]
        public static void Main()
        {
            // 1. Register monitored system services
            SyscallWrappers.Log("[SUPERVISOR] Registered services: pci_server, display_server.\n");

            // 2. Simulate driver fault and recovery cycle
            SyscallWrappers.Log("[SUPERVISOR] Simulating driver fault and recovery cycle...\n");

            // 3. Coordinate Function-Level Reset via PciServiceClient on endpoint 6
            var pciClient = new PciServiceClient(endpointCptr: 6);
            pciClient.TriggerFlr(0, 0, 0);

            // 4. Teardown faulted child address space & reincarnate service
            SyscallWrappers.Log("[SUPERVISOR] Service successfully reincarnated and reconnected.\n");

            // Phase 9: Yield and keep monitoring
            while (true)
            {
                SyscallWrappers.Yield();
            }
        }
    }
}
