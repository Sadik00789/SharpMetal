namespace System
{
    public class Object
    {
        public Object() { }
        ~Object() { }
        public virtual bool Equals(object? obj) => this == obj;
        public virtual int GetHashCode() => 0;
        public virtual string? ToString() => null;
        public Type GetType() => null!;
        protected object MemberwiseClone() => this;
    }
}
