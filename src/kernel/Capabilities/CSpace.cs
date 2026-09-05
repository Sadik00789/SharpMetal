using Microkernel.Abstractions.Capabilities;

namespace Kernel.Capabilities
{
    public static unsafe class CSpace
    {
        public const ulong ErrSuccess           = 0;
        public const ulong ErrInvalidCapability = 0xFFFFFFFFFFFFFFFEUL; // -2
        public const ulong ErrPermissionDenied   = 0xFFFFFFFFFFFFFFFDUL; // -3

        public static ulong LookupCapability(CNode* root, uint cptr, CapabilityRights requiredRights, out Capability* cap)
        {
            cap = null;
            if (root == null || cptr >= CNode.SlotCount)
            {
                return ErrInvalidCapability;
            }

            Capability* c = root->Get(cptr);
            if (c == null || c->IsNull)
            {
                return ErrInvalidCapability;
            }

            if ((c->Rights & requiredRights) != requiredRights)
            {
                return ErrPermissionDenied;
            }

            cap = c;
            return ErrSuccess;
        }
    }
}
