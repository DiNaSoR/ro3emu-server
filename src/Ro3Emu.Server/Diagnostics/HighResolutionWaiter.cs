using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>Waits for queue work or a precise deadline without changing system timer resolution.
/// Windows 10 1803+ supplies CREATE_WAITABLE_TIMER_HIGH_RESOLUTION; other platforms use their normal wait clock.</summary>
internal sealed class HighResolutionWaiter : IDisposable
{
    private readonly AutoResetEvent _work;
    private readonly EventWaitHandle? _timer;
    private readonly WaitHandle[]? _handles;
    public bool UsesHighResolutionTimer => _timer != null;
    public HighResolutionWaiter(AutoResetEvent work)
    {
        _work = work;
        if (!OperatingSystem.IsWindows()) return;
        var handle = CreateWaitableTimerExW(0, null, 2 /* HIGH_RESOLUTION */, 0x1F0003 /* TIMER_ALL_ACCESS */);
        if (handle.IsInvalid) { handle.Dispose(); return; }
        _timer = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
        _handles = [work, _timer];
    }
    public void Wait(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return;
        if (_timer != null)
        {
            long due = -Math.Max(1, remaining.Ticks);
            if (!SetWaitableTimerEx(_timer.SafeWaitHandle, ref due, 0, 0, 0, 0, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            WaitHandle.WaitAny(_handles!);
        }
        else
        {
            // No global timeBeginPeriod side effect. On legacy Windows this fallback is less precise and is visible
            // in cadenceMs; the supported Windows load-test host uses the high-resolution handle above.
            _work.WaitOne(remaining);
        }
    }
    public void Dispose() => _timer?.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long due, int period, nint completion, nint argument, nint reason, uint tolerance);
}
