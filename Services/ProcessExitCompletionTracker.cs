namespace GameOrchestrator.Services;

public enum ProcessExitCheck { Waiting, Completed, StartupTimedOut }

// Elapsed time comes from a monotonic clock, so changing the system clock cannot
// shorten the startup deadline or the exit debounce period.
public sealed class ProcessExitCompletionTracker(bool targetStarted = false)
{
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private bool _seen = targetStarted;
    private TimeSpan? _missingSince;

    public ProcessExitCheck Check(bool present, TimeSpan elapsed)
    {
        if (present)
        {
            _seen = true;
            _missingSince = null;
        }
        else if (_seen)
        {
            _missingSince ??= elapsed;
            if (elapsed - _missingSince >= TimeSpan.FromSeconds(3)) return ProcessExitCheck.Completed;
        }
        else if (elapsed >= StartupTimeout) return ProcessExitCheck.StartupTimedOut;
        return ProcessExitCheck.Waiting;
    }
}
