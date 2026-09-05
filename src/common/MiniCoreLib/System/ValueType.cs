namespace System
{
    public abstract class ValueType
    {
        public override bool Equals(object? obj) => false;
        public override int GetHashCode() => 0;
        public override string? ToString() => null;
    }

    public abstract class Enum : ValueType
    {
    }
}
