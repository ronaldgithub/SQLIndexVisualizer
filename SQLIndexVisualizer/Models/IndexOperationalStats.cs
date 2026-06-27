namespace SQLIndexVisualizer.Models;

public record IndexOperationalStats(
    long LeafAllocations,
    long NonLeafAllocations,
    long RowLockWaits,
    long PageLockWaits,
    long RowLockWaitMs,
    long PageLockWaitMs);
