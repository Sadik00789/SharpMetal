using System;

namespace PciServer.Pci
{
    public unsafe struct PciDevice
    {
        public uint Bus;
        public uint Device;
        public uint Function;
        public ushort VendorId;
        public ushort DeviceId;
        public byte BaseClass;
        public byte SubClass;
        public ulong ConfigVirtAddress;
        public byte MsiOffset;
        public byte MsiXOffset;
        public ulong Bar0;
    }
}
