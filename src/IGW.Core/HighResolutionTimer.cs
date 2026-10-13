using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IGW.Core;

/// <summary>Windows 高精度可等待计时器；一个设备复用一个句柄，不忙等、不长期占用 CPU。</summary>
internal sealed class HighResolutionWaiter : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private readonly NativeWaitHandle? _timer;
    private WaitHandle[]? _waitHandles;

    public bool IsAvailable => _timer is not null;

    public HighResolutionWaiter()
    {
        if (!OperatingSystem.IsWindows()) return;
        nint handle = CreateWaitableTimerEx(nint.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (handle != nint.Zero) _timer = new NativeWaitHandle(handle);
    }

    public bool Wait(int milliseconds, CancellationToken cancellationToken)
    {
        if (_timer is null) return false;
        long dueTime100Ns = -Math.Max(1L, milliseconds) * 10_000L;
        if (!SetWaitableTimer(_timer.SafeWaitHandle, ref dueTime100Ns, 0, nint.Zero, nint.Zero, false))
            return false;
        _waitHandles ??= new WaitHandle[] { _timer, cancellationToken.WaitHandle };
        return WaitHandle.WaitAny(_waitHandles) == 0;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _waitHandles = null;
    }

    private sealed class NativeWaitHandle : WaitHandle
    {
        public NativeWaitHandle(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerEx(
        nint timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        SafeWaitHandle timer,
        ref long dueTime,
        int period,
        nint completionRoutine,
        nint argToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
}
