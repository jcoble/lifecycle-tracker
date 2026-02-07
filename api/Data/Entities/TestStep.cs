using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class TestStep
{
    public int Id { get; set; }
    public int TestPlanId { get; set; }
    public int OrderIndex { get; set; }
    public TestStepType StepType { get; set; }
    public required string Description { get; set; }
    public string? ExpectedResult { get; set; }
    public string? AutomationCommand { get; set; }
    public bool RequiresManualVerification { get; set; }
    public DateTime CreatedAt { get; set; }
    public TestPlan TestPlan { get; set; } = null!;
    public ICollection<TestStepResult> Results { get; set; } = new List<TestStepResult>();
}
