namespace System
{
    public class String
    {
        private readonly IntPtr _methodTable;
        private readonly int _stringLength;
        private readonly char _firstChar;

        public int Length => _stringLength;

        public unsafe char this[int index]
        {
            get
            {
                fixed (char* p = &_firstChar)
                {
                    return p[index];
                }
            }
        }

        public static unsafe bool operator ==(String a, String b)
        {
            if ((object)a == null) return (object)b == null;
            if ((object)b == null) return false;
            if (a._stringLength != b._stringLength) return false;

            fixed (char* ap = &a._firstChar)
            fixed (char* bp = &b._firstChar)
            {
                for (int i = 0; i < a._stringLength; i++)
                {
                    if (ap[i] != bp[i]) return false;
                }
            }
            return true;
        }

        public static bool operator !=(String a, String b)
        {
            return !(a == b);
        }

        public override bool Equals(object? obj) => obj is string s && this == s;
        public override int GetHashCode() => _stringLength;
    }
}
