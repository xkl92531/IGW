namespace IGW.Abstractions;

/// <summary>一次协议操作失败所影响的最小范围。</summary>
public enum DriverFailureScope
{
    Point,
    Block,
    Capacity,
    Session,
    Transport,
    Overload,
}

/// <summary>运行引擎对失败应采取的动作，而不是根据“是否抛异常”猜测。</summary>
public enum DriverRetryDisposition
{
    DoNotRetry,
    RetrySameConnection,
    ReduceBatchAndRetry,
    RebuildSession,
    RebuildConnection,
}

/// <summary>驱动已完成协议级判断的操作异常。</summary>
public sealed class DriverOperationException : Exception
{
    public DriverFailureScope Scope { get; }
    public DriverRetryDisposition Retry { get; }
    public StatusCode Status { get; }

    public DriverOperationException(
        string message,
        DriverFailureScope scope,
        DriverRetryDisposition retry,
        StatusCode status,
        Exception? inner = null)
        : base(message, inner)
    {
        Scope = scope;
        Retry = retry;
        Status = status;
    }
}

/// <summary>运行引擎可直接执行的协议操作失败判定。</summary>
public readonly record struct DriverOperationFailure(
    DriverFailureScope Scope,
    DriverRetryDisposition Retry,
    StatusCode Status,
    bool ConnectionLost,
    string Message)
{
    public static DriverOperationFailure FromException(Exception error, ConnectionState driverState)
    {
        bool timeout = false;
        bool valueFailure = false;
        bool permissionFailure = false;

        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is DriverOperationException explicitFailure)
            {
                bool lost = explicitFailure.Retry is
                    DriverRetryDisposition.RebuildSession or
                    DriverRetryDisposition.RebuildConnection;
                return new DriverOperationFailure(
                    explicitFailure.Scope,
                    explicitFailure.Retry,
                    explicitFailure.Status,
                    lost,
                    explicitFailure.Message);
            }

            if (current is System.Net.Sockets.SocketException or IOException)
                return new DriverOperationFailure(
                    DriverFailureScope.Transport,
                    DriverRetryDisposition.RebuildConnection,
                    StatusCode.BadCommFailure,
                    true,
                    current.Message);

            if (current is TimeoutException or OperationCanceledException)
                timeout = true;
            else if (current is UnauthorizedAccessException)
                permissionFailure = true;
            else if (current is ArgumentException or InvalidDataException or FormatException or
                     InvalidCastException or OverflowException or NotSupportedException)
                valueFailure = true;
        }

        if (driverState is ConnectionState.Disconnected or ConnectionState.Faulted)
            return new DriverOperationFailure(
                DriverFailureScope.Transport,
                DriverRetryDisposition.RebuildConnection,
                StatusCode.BadCommFailure,
                true,
                error.Message);

        if (timeout)
            return new DriverOperationFailure(
                DriverFailureScope.Block,
                DriverRetryDisposition.RetrySameConnection,
                StatusCode.BadTimeout,
                false,
                error.Message);

        if (permissionFailure)
            return new DriverOperationFailure(
                DriverFailureScope.Point,
                DriverRetryDisposition.DoNotRetry,
                StatusCode.BadNotWritable,
                false,
                error.Message);

        if (valueFailure)
            return new DriverOperationFailure(
                DriverFailureScope.Point,
                DriverRetryDisposition.DoNotRetry,
                StatusCode.BadDecodeError,
                false,
                error.Message);

        return new DriverOperationFailure(
            DriverFailureScope.Block,
            DriverRetryDisposition.DoNotRetry,
            StatusCode.BadDeviceFailure,
            false,
            error.Message);
    }
}
