namespace Microkernel.Abstractions.Rpc
{
    public readonly struct RpcResult<T>
    {
        public ulong Status { get; }
        public T Value { get; }

        public bool IsSuccess => Status == 0;

        public RpcResult(ulong status, T value)
        {
            Status = status;
            Value = value;
        }

        public static RpcResult<T> Success(T value)
        {
            return new RpcResult<T>(0, value);
        }

        public static RpcResult<T> Failure(ulong status)
        {
            return new RpcResult<T>(status, default!);
        }
    }
}
