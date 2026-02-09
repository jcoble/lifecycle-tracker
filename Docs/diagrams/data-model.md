# Lifecycle Tracker — Data Model

```mermaid
erDiagram
    Project ||--o{ Milestone : contains
    Project ||--o{ Label : has
    Project ||--o{ TeamMember : has
    Project ||--o{ ActivityLog : tracks

    Milestone ||--o{ Phase : contains

    Phase ||--o{ LifecycleTask : contains

    LifecycleTask ||--o{ TestPlan : has
    LifecycleTask ||--o{ Attachment : has
    LifecycleTask ||--o{ Comment : has
    LifecycleTask ||--o{ TaskLabel : tagged
    LifecycleTask ||--o{ TaskAssignment : assigned

    TestPlan ||--o{ Test : contains
    TestPlan ||--o{ TestExecution : runs

    Test ||--o{ TestStep : contains

    TestExecution ||--o{ TestStepResult : records
    TestStep ||--o{ TestStepResult : results

    TeamMember ||--o{ AgentSession : spawns
    TeamMember ||--o{ TaskAssignment : receives

    Project {
        int Id PK
        string Name
        ProjectStatus Status
        string Settings_JSON
    }

    Milestone {
        int Id PK
        string Name
        string Version
        MilestoneStatus Status
        date TargetDate
    }

    Phase {
        int Id PK
        string Name
        string Goal
        string SuccessCriteria
        PhaseStatus Status
        int PhaseNumber
        string DependsOnPhaseIds
    }

    LifecycleTask {
        int Id PK
        string Title
        TaskStatus Status
        TaskPriority Priority
        TaskType Type
        TestLevel RequiredTestLevel
        bool SkipUiTesting
        string GitCommitSha
        string GitBranch
    }

    TestPlan {
        int Id PK
        string Name
        TestLevel RequiredLevel
        TestPlanStatus Status
    }

    Test {
        int Id PK
        string Name
        TestType Type
        TestStatus Status
        string TestFile
        string Framework
    }

    TestStep {
        int Id PK
        TestStepType StepType
        string Description
        string ExpectedResult
        string AutomationCommand
    }

    TeamMember {
        int Id PK
        string AgentName
        string Role
        string ModelName
        string TriggerStatuses_JSON
        string SpawnPromptTemplate
    }
```

## Key Enums

| Enum | Values |
|------|--------|
| TaskStatus | Backlog, Todo, InProgress, Review, Blocked, Done, Cancelled |
| TaskPriority | P1, P2, P3, P4 |
| TaskType | Feature, Bug, Refactor, Docs, Test, Infra, Research |
| PhaseStatus | NotStarted, Planning, InProgress, Review, Blocked, Completed, Cancelled |
| MilestoneStatus | Planning, InProgress, OnHold, Completed, Cancelled |
| TestLevel | Smoke, Comprehensive, FullE2E |
| TestType | Unit, Integration, UI, Manual |
| TestExecutionMode | Manual, AI_AgentBrowser, Playwright, Unit_xUnit, Unit_Vitest |
| TestStepType | Setup, Action, Assertion, Teardown |
