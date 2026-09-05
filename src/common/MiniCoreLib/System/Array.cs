namespace System
{
    public class Array
    {
        private int _length;
        public int Length => _length;
    }

    public class Array<T> : Array
    {
    }
}
