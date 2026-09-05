namespace System
{
    public struct Void { }
    public struct Boolean { }
    public struct Char { }
    public struct SByte { }
    public struct Byte { }
    public struct Int16 { }
    public struct UInt16 { }
    public struct Int32 { }
    public struct UInt32 { }
    public struct Int64 { }
    public struct UInt64 { }
    public struct IntPtr
    {
        private readonly unsafe void* _value;
        public static readonly IntPtr Zero = default;
        public unsafe IntPtr(void* value) => _value = value;
        public unsafe IntPtr(long value) => _value = (void*)value;
        public unsafe IntPtr(ulong value) => _value = (void*)value;
        public static unsafe explicit operator void*(IntPtr value) => value._value;
        public static unsafe explicit operator IntPtr(void* value) => new IntPtr(value);
        public static unsafe explicit operator long(IntPtr value) => (long)value._value;
        public static unsafe explicit operator ulong(IntPtr value) => (ulong)value._value;
        public static unsafe explicit operator IntPtr(long value) => new IntPtr((void*)value);
        public static unsafe explicit operator IntPtr(ulong value) => new IntPtr((void*)value);
        public unsafe void* ToPointer() => _value;
    }

    public struct UIntPtr
    {
        private readonly unsafe void* _value;
        public static readonly UIntPtr Zero = default;
        public unsafe UIntPtr(void* value) => _value = value;
        public unsafe UIntPtr(ulong value) => _value = (void*)value;
        public static unsafe explicit operator void*(UIntPtr value) => value._value;
        public static unsafe explicit operator UIntPtr(void* value) => new UIntPtr(value);
        public static unsafe explicit operator ulong(UIntPtr value) => (ulong)value._value;
        public static unsafe explicit operator UIntPtr(ulong value) => new UIntPtr((void*)value);
        public unsafe void* ToPointer() => _value;
    }

    public struct Single { }
    public struct Double { }

    public class Type { }
    public abstract class Delegate { }
    public abstract class MulticastDelegate : Delegate { }
    public class Exception
    {
        public string? Message { get; }
        public Exception() { }
        public Exception(string? message) => Message = message;
    }

    public struct RuntimeTypeHandle { }
    public struct RuntimeFieldHandle { }
    public struct RuntimeMethodHandle { }

    public class Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = true)]
    public sealed class AttributeUsageAttribute : Attribute
    {
        public AttributeUsageAttribute(AttributeTargets validOn) => ValidOn = validOn;
        public AttributeTargets ValidOn { get; }
        public bool AllowMultiple { get; set; }
        public bool Inherited { get; set; }
    }

    [Flags]
    public enum AttributeTargets
    {
        Assembly = 1,
        Module = 2,
        Class = 4,
        Struct = 8,
        Enum = 16,
        Constructor = 32,
        Method = 64,
        Property = 128,
        Field = 256,
        Event = 512,
        Interface = 1024,
        Parameter = 2048,
        Delegate = 4096,
        ReturnValue = 8192,
        GenericParameter = 16384,
        All = 32767
    }

    [AttributeUsage(AttributeTargets.Enum)]
    public sealed class FlagsAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class ParamArrayAttribute : Attribute { }
}

namespace System.Runtime.InteropServices
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnmanagedCallersOnlyAttribute : Attribute
    {
        public string? EntryPoint;
        public Type[]? CallConvs;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class StructLayoutAttribute : Attribute
    {
        public StructLayoutAttribute(LayoutKind layoutKind) => Value = layoutKind;
        public LayoutKind Value { get; }
        public int Pack;
        public int Size;
    }

    public enum LayoutKind
    {
        Sequential = 0,
        Explicit = 2,
        Auto = 3
    }

    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public sealed class FieldOffsetAttribute : Attribute
    {
        public FieldOffsetAttribute(int offset) => Value = offset;
        public int Value { get; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class DllImportAttribute : Attribute
    {
        public DllImportAttribute(string dllName) => Value = dllName;
        public string Value { get; }
        public string? EntryPoint;
        public CallingConvention CallingConvention;
    }

    public enum CallingConvention
    {
        Winapi = 1,
        Cdecl = 2,
        StdCall = 3,
        ThisCall = 4,
        FastCall = 5
    }

    public enum UnmanagedType
    {
        Bool = 2,
        I1 = 3,
        U1 = 4,
        I2 = 5,
        U2 = 6,
        I4 = 7,
        U4 = 8,
        I8 = 9,
        U8 = 10,
        R4 = 11,
        R8 = 12,
        LPStr = 20,
        LPWStr = 21,
        FunctionPtr = 38,
        SysInt = 31,
        SysUInt = 32
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class InAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class OutAttribute : Attribute { }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class CompilerGeneratedAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All)]
    public sealed class IsUnmanagedAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class UnsafeValueTypeAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
    public sealed class MethodImplAttribute : Attribute
    {
        public MethodImplAttribute(MethodImplOptions methodImplOptions) => Value = methodImplOptions;
        public MethodImplOptions Value { get; }
    }

    [Flags]
    public enum MethodImplOptions
    {
        NoInlining = 8,
        NoOptimization = 64,
        AggressiveInlining = 256,
        AggressiveOptimization = 512
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class CallerMemberNameAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class CallerFilePathAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class CallerLineNumberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
    public sealed class InternalsVisibleToAttribute : Attribute
    {
        public InternalsVisibleToAttribute(string assemblyName) => AssemblyName = assemblyName;
        public string AssemblyName { get; }
    }

    public class CallConvCdecl { }
    public class CallConvStdcall { }
    public class CallConvThiscall { }
    public class CallConvFastcall { }
    public class CallConvMemberFunction { }
    public static class IsVolatile { }

    public static class RuntimeHelpers
    {
        public static unsafe int OffsetToStringData => sizeof(void*) + sizeof(int);
    }
}

namespace System.Reflection
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    public sealed class DefaultMemberAttribute : Attribute
    {
        public DefaultMemberAttribute(string memberName) => MemberName = memberName;
        public string MemberName { get; }
    }
}
