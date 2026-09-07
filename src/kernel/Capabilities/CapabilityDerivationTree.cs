using System;
using Kernel.Concurrency;
using Kernel.Memory.Heap;
using Kernel.Memory.Virtual;
using Microkernel.Abstractions.Capabilities;

namespace Kernel.Capabilities
{
    public unsafe struct CdtNode
    {
        public CNode* CNode;
        public uint Slot;
        public CdtNode* Parent;
        public CdtNode* FirstChild;
        public CdtNode* NextSibling;
        public CdtNode* PrevSibling;
    }

    public static unsafe class CapabilityDerivationTree
    {
        private static SpinLockWithIrqSave s_cdtLock;

        public static CdtNode* CreateNode(CNode* cnode, uint slot)
        {
            ulong rflags = s_cdtLock.Acquire();
            try
            {
                CdtNode* node = (CdtNode*)SlabAllocator.KmAlloc((ulong)sizeof(CdtNode));
                if (node == null) return null;
                node->CNode = cnode;
                node->Slot = slot;
                node->Parent = null;
                node->FirstChild = null;
                node->NextSibling = null;
                node->PrevSibling = null;
                return node;
            }
            finally
            {
                s_cdtLock.Release(rflags);
            }
        }

        public static void Derive(CdtNode* parent, CdtNode* child)
        {
            if (parent == null || child == null) return;

            ulong rflags = s_cdtLock.Acquire();
            try
            {
                child->Parent = parent;
                child->NextSibling = parent->FirstChild;
                child->PrevSibling = null;
                if (parent->FirstChild != null)
                {
                    parent->FirstChild->PrevSibling = child;
                }
                parent->FirstChild = child;
            }
            finally
            {
                s_cdtLock.Release(rflags);
            }
        }

        public static void Revoke(CdtNode* node)
        {
            if (node == null) return;

            ulong rflags = s_cdtLock.Acquire();
            try
            {
                RevokeInternal(node);
            }
            finally
            {
                s_cdtLock.Release(rflags);
            }
        }

        private static void RevokeInternal(CdtNode* node)
        {
            if (node == null) return;

            CdtNode* currChild = node->FirstChild;
            while (currChild != null)
            {
                CdtNode* next = currChild->NextSibling;
                RevokeInternal(currChild);
                DeleteInternal(currChild);
                currChild = next;
            }
            node->FirstChild = null;
        }

        public static void Delete(CdtNode* node)
        {
            if (node == null) return;

            ulong rflags = s_cdtLock.Acquire();
            try
            {
                DeleteInternal(node);
            }
            finally
            {
                s_cdtLock.Release(rflags);
            }
        }

        private static void DeleteInternal(CdtNode* node)
        {
            if (node == null) return;

            // Recurse through derived child nodes first
            RevokeInternal(node);

            // Unlink from sibling / parent list
            if (node->PrevSibling != null)
            {
                node->PrevSibling->NextSibling = node->NextSibling;
            }
            else if (node->Parent != null && node->Parent->FirstChild == node)
            {
                node->Parent->FirstChild = node->NextSibling;
            }

            if (node->NextSibling != null)
            {
                node->NextSibling->PrevSibling = node->PrevSibling;
            }

            // Unmap active page frames bound to this capability
            if (node->CNode != null && node->Slot < CNode.SlotCount)
            {
                Capability* cap = node->CNode->Get(node->Slot);
                if (cap != null && !cap->IsNull)
                {
                    if ((cap->Type == CapabilityType.Frame || cap->Type == CapabilityType.VirtualPage) && 
                        cap->MappedVirtualAddress != 0 && cap->OwnerProcess != null)
                    {
                        VirtualMemorySpace.UnmapPage(cap->OwnerProcess->PageDirectoryPhysBase, cap->MappedVirtualAddress);
                        cap->MappedVirtualAddress = 0;
                        cap->OwnerProcess = null;
                    }
                    node->CNode->Revoke(node->Slot);
                }
            }

            SlabAllocator.KmFree(node, (ulong)sizeof(CdtNode));
        }

        public static void RevokeSlot(CNode* cnode, uint slot)
        {
            if (cnode == null || slot >= CNode.SlotCount) return;
            ulong rflags = s_cdtLock.Acquire();
            try
            {
                Capability* cap = cnode->Get(slot);
                if (cap == null || cap->IsNull) return;

                if ((cap->Type == CapabilityType.Frame || cap->Type == CapabilityType.VirtualPage) && 
                    cap->MappedVirtualAddress != 0 && cap->OwnerProcess != null)
                {
                    VirtualMemorySpace.UnmapPage(cap->OwnerProcess->PageDirectoryPhysBase, cap->MappedVirtualAddress);
                    cap->MappedVirtualAddress = 0;
                    cap->OwnerProcess = null;
                }
            }
            finally
            {
                s_cdtLock.Release(rflags);
            }
        }
    }
}
