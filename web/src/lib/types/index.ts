export type ProjectStatus = 'Active' | 'OnHold' | 'Completed' | 'Archived';
export type MilestoneStatus = 'Planning' | 'InProgress' | 'OnHold' | 'Completed' | 'Cancelled';
export type PhaseStatus = 'NotStarted' | 'Planning' | 'InProgress' | 'Review' | 'Blocked' | 'Completed' | 'Cancelled';
export type TaskStatus = 'Backlog' | 'Todo' | 'InProgress' | 'Review' | 'Blocked' | 'Done' | 'Cancelled';
export type TaskPriority = 'P1' | 'P2' | 'P3' | 'P4';
export type TaskType = 'Feature' | 'Bug' | 'Refactor' | 'Docs' | 'Test' | 'Infra' | 'Research';
export type TaskSource = 'Manual' | 'Claude';
export type TestType = 'Unit' | 'Integration' | 'EndToEnd' | 'Manual';
export type TestStatus = 'NotCreated' | 'Created' | 'Passing' | 'Failing' | 'Skipped';
export type CommentSource = 'Manual' | 'Claude' | 'System';
export type TestLevel = 'Smoke' | 'Functional' | 'Comprehensive' | 'FullE2E';
export type TestPlanStatus = 'Draft' | 'ReadyForExecution' | 'InProgress' | 'Passing' | 'Failing';
export type TestPlanSource = 'Manual' | 'AI_Generated' | 'Template';
export type TestStepType = 'Setup' | 'Action' | 'Assertion' | 'Teardown';
export type TestExecutionMode = 'Manual' | 'AI_AgentBrowser' | 'Playwright' | 'Unit_xUnit' | 'Unit_Vitest';
export type TestExecutionStatus = 'NotStarted' | 'Running' | 'Passed' | 'Failed' | 'Blocked' | 'Cancelled';
export type TestStepStatus = 'Passed' | 'Failed' | 'Skipped' | 'Blocked';
export type TestAutonomyLevel = 'Manual' | 'SemiAuto' | 'AutoCreate' | 'FullAuto';

export interface Project {
	id: number;
	name: string;
	description?: string;
	repository?: string;
	status: ProjectStatus;
	settings?: string;
	createdAt: string;
	updatedAt: string;
	milestonesCount?: number;
}

export interface ProjectSettings {
	language?: string;
	framework?: string;
	repositoryRoot?: string;
	devUrl?: string;
	apiUrl?: string;
	unitTestCommand?: string;
	integrationTestCommand?: string;
	webTestTool?: string;
	[key: string]: string | undefined;
}

export interface Milestone {
	id: number;
	projectId: number;
	name: string;
	description?: string;
	version?: string;
	status: MilestoneStatus;
	orderIndex: number;
	startedAt?: string;
	targetDate?: string;
	completedAt?: string;
	createdAt: string;
	updatedAt: string;
	phases?: Phase[];
}

export interface Phase {
	id: number;
	milestoneId: number;
	name: string;
	description?: string;
	goal?: string;
	successCriteria?: string;
	status: PhaseStatus;
	phaseNumber: number;
	orderIndex: number;
	dependsOnPhaseIds?: number[];
	startedAt?: string;
	completedAt?: string;
	createdAt: string;
	updatedAt: string;
	tasks?: Task[];
	taskCount?: number;
	doneCount?: number;
}

export interface Task {
	id: number;
	phaseId?: number;
	title: string;
	description?: string;
	status: TaskStatus;
	priority: TaskPriority;
	type: TaskType;
	source: TaskSource;
	orderInColumn: number;
	dueDate?: string;
	startedAt?: string;
	completedAt?: string;
	gitCommitSha?: string;
	gitBranch?: string;
	pullRequestUrl?: string;
	conversationRef?: string;
	requiredTestLevel?: TestLevel;
	createdAt: string;
	updatedAt: string;
	labels?: Label[];
	tests?: TestRecord[];
	testPlans?: TestPlan[];
	attachments?: Attachment[];
	comments?: Comment[];
	phaseName?: string;
	assignedTo?: {
		teamMemberId: number;
		agentName: string;
	};
}

export interface TestRecord {
	id: number;
	taskId: number;
	testType: TestType;
	status: TestStatus;
	testName?: string;
	testFile?: string;
	framework?: string;
	lastRunAt?: string;
	lastRunResult?: string;
	lastRunOutput?: string;
	totalRuns: number;
	passedRuns: number;
	failedRuns: number;
	createdAt: string;
}

export interface Attachment {
	id: number;
	taskId: number;
	fileName: string;
	originalFileName: string;
	contentType: string;
	fileSize: number;
	storagePath: string;
	width?: number;
	height?: number;
	uploadedBy: string;
	uploadedAt: string;
}

export interface Comment {
	id: number;
	taskId: number;
	content: string;
	source: CommentSource;
	author?: string;
	createdAt: string;
	updatedAt: string;
}

export interface Label {
	id: number;
	projectId: number;
	name: string;
	color: string;
	description?: string;
}

export interface ActivityLog {
	id: number;
	projectId: number;
	type: string;
	source: string;
	entityType?: string;
	entityId?: number;
	action?: string;
	changes?: string;
	description?: string;
	actor?: string;
	createdAt: string;
}

export interface DashboardPhase {
	id: number;
	name: string;
	phaseNumber: number;
	status: PhaseStatus;
	taskCount: number;
	doneCount: number;
	goal?: string;
}

export interface DashboardMilestone {
	id: number;
	name: string;
	version?: string;
	status: MilestoneStatus;
	phaseCount: number;
	targetDate?: string;
	orderIndex?: number;
}

export interface TestPlan {
	id: number;
	taskId: number;
	name: string;
	description?: string;
	requiredLevel: TestLevel;
	status: TestPlanStatus;
	source: TestPlanSource;
	stepCount?: number;
	createdAt: string;
	updatedAt: string;
	steps?: TestStep[];
	executions?: TestExecution[];
	latestExecution?: {
		id: number;
		status: TestExecutionStatus;
		passedSteps: number;
		failedSteps: number;
		totalSteps: number;
		startedAt: string;
		completedAt?: string;
	};
}

export interface TestStep {
	id: number;
	testPlanId: number;
	orderIndex: number;
	stepType: TestStepType;
	description: string;
	expectedResult?: string;
	automationCommand?: string;
	requiresManualVerification: boolean;
	createdAt: string;
}

export interface TestExecution {
	id: number;
	testPlanId: number;
	executionMode: TestExecutionMode;
	status: TestExecutionStatus;
	totalSteps: number;
	passedSteps: number;
	failedSteps: number;
	skippedSteps: number;
	executedBy?: string;
	startedAt: string;
	completedAt?: string;
	failureReason?: string;
	stepResults?: TestStepResultItem[];
}

export interface TestStepResultItem {
	id: number;
	testStepId: number;
	status: TestStepStatus;
	actualResult?: string;
	errorMessage?: string;
	screenshot?: string;
	durationMs: number;
	executedAt: string;
	stepDescription?: string;
}

export interface TeamMember {
	id: number;
	projectId: number;
	role: string;
	agentName: string;
	modelName: string;
	isPersistent: boolean;
	status: string;
	configJson?: string;
	spawnPromptTemplate?: string;
	triggerStatuses?: string;
	createdAt: string;
	lastActiveAt?: string;
	currentSession?: {
		id: number;
		sessionId: string;
		status: string;
		spawnedAt: string;
	};
	currentTask?: {
		taskId: number;
		status: string;
	};
}

export interface AgentSession {
	id: number;
	sessionId: string;
	status: string;
	spawnedAt: string;
	completedAt?: string;
	tokensUsed?: number;
}

export interface TaskAssignmentInfo {
	id: number;
	taskId: number;
	taskTitle?: string;
	assignedBy: string;
	status: string;
	assignedAt: string;
	completedAt?: string;
}

export interface AgentEscalation {
	id: number;
	projectId: number;
	taskId?: number;
	taskTitle?: string;
	description: string;
	status: string;
	resolution?: string;
	createdAt: string;
	resolvedAt?: string;
}

export interface Dashboard {
	project: Project;
	activeMilestone?: DashboardMilestone;
	milestones: DashboardMilestone[];
	phases: DashboardPhase[];
	taskSummary: {
		total: number;
		byStatus: Record<string, number>;
		bySource: Record<string, number>;
	};
	testSummary: {
		total: number;
		passing: number;
		failing: number;
		notCreated: number;
	};
	recentActivity: ActivityLog[];
	labels: Label[];
}
