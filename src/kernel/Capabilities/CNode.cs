using Kernel.Memory.Heap;
using Kernel.Memory.Physical;
using Kernel.Memory.Virtual;
using Microkernel.Abstractions.Capabilities;

namespace Kernel.Capabilities
{
    public unsafe struct CNode
    {
        public const int SlotCount = 256;
        public Capability* Slots;

        public static CNode* Create()
        {
            // 256 slots * 32 bytes = 8192 bytes (2 contiguous 4K physical pages)
            ulong phys = PageFrameAllocator.AllocateContiguousFrames(2);
            if (phys == 0) return null;

            Capability* slots = (Capability*)Hhdm.PhysicalToVirtual(phys);

            // Zero out memory
            ulong* words = (ulong*)slots;
            int totalWords = (SlotCount * sizeof(Capability)) / sizeof(ulong);
            for (int i = 0; i < totalWords; i++)
            {
                words[i] = 0;
            }

            CNode* cnode = (CNode*)SlabAllocator.KmAlloc(32); // size class 32 holds CNode struct
            cnode->Slots = slots;
            return cnode;
        }

        public bool Set(uint slot, void* target, CapabilityType type, CapabilityRights rights, ulong badge = 0)
        {
            if (slot >= SlotCount || Slots == null) return false;

            Slots[slot].TargetObject = target;
            Slots[slot].Type = type;
            Slots[slot].Rights = rights;
            Slots[slot].Badge = badge;
            Slots[slot].Reserved = 0;
            return true;
        }

        public Capability* Get(uint slot)
        {
            if (slot >= SlotCount || Slots == null) return null;
            return &Slots[slot];
        }

        public void Revoke(uint slot)
        {
            if (slot < SlotCount && Slots != null)
            {
                Slots[slot].TargetObject = null;
                Slots[slot].Type = CapabilityType.Null;
                Slots[slot].Rights = CapabilityRights.None;
                Slots[slot].Badge = 0;
                Slots[slot].Reserved = 0;
            }
        }
    }
}
