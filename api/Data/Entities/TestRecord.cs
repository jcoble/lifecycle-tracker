using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class TestRecord
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public TestType TestType { get; set; }
    public TestStatus Status { get; set; }
    public string? TestName { get; set; }
    public string? TestFile { get; set; }
    public string? Framework { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunResult { get; set; }
    public string? LastRunOutput { get; set; }
    public int TotalRuns { get; set; }
    public int PassedRuns { get; set; }
    public int FailedRuns { get; set; }
    public DateTime CreatedAt { get; set; }
    public LifecycleTask Task { get; set; } = null!;
}
