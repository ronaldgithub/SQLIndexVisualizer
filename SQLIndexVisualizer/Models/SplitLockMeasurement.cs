using System;

namespace SQLIndexVisualizer.Models;

public class SplitLockMeasurement
{
    public string   Label      { get; set; } = string.Empty;
    public long     PageSplits { get; set; }
    public long     LockWaits  { get; set; }
    public long     LockWaitMs { get; set; }
    public DateTime MeasuredAt { get; set; }

    public string TimeDisplay => MeasuredAt.ToString("HH:mm:ss");
    public string WaitDisplay => LockWaitMs == 0 ? "—" : $"{LockWaitMs:N0} ms";
}
