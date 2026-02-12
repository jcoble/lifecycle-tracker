import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActiveProjectId } from '../api-client.js';
import { setLastActivity } from '../index.js';

interface ProjectSettings {
  id: number;
  settings: Record<string, string>;
}

export function registerTaskTools(server: McpServer) {
  server.tool(
    'create_tasks',
    'Create multiple tasks at once from a conversation breakdown',
    {
      projectId: z.number().optional().describe('Project ID for activity logging (uses active project if omitted)'),
      phaseId: z.number().optional().describe('Phase ID to assign tasks to'),
      tasks: z.array(z.object({
        title: z.string(),
        description: z.string().optional(),
        priority: z.enum(['P1', 'P2', 'P3', 'P4']).default('P3'),
        type: z.enum(['Feature', 'Bug', 'Refactor', 'Docs', 'Test', 'Infra', 'Research']).default('Feature'),
        labelIds: z.array(z.number()).optional(),
      })).describe('Array of tasks to create'),
    },
    async ({ projectId, phaseId, tasks }) => {
      setLastActivity(`Creating ${tasks.length} task(s)`);
      const pid = projectId ?? getActiveProjectId();
      const result = await api.post('/ai/tasks/bulk-create', { projectId: pid, phaseId, tasks });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result) }] };
    }
  );

  server.tool(
    'start_task',
    'Move a task to InProgress status. Returns testing guidance.',
    {
      taskId: z.number().describe('Task ID to start'),
    },
    async ({ taskId }) => {
      setLastActivity(`Starting task #${taskId}`);

      // Check current status — if Backlog, step through Todo first
      try {
        const task = await api.get<{ id: number; status: string }>(`/tasks/${taskId}`);
        if (task.status === 'Backlog') {
          await api.post(`/ai/tasks/${taskId}/transition`, { status: 'Todo' });
        }
      } catch {
        // If we can't fetch, try the direct transition and let the API handle it
      }

      const result = await api.post(`/ai/tasks/${taskId}/transition`, { status: 'InProgress' });

      // Fetch task type for type-specific guidance
      let taskType = 'Feature';
      try {
        const taskInfo = await api.get<{ type: string }>(`/tasks/${taskId}`);
        taskType = taskInfo.type;
      } catch { /* use default */ }

      // Return type-specific workflow guidance
      let workflowNote = '';
      if (taskType === 'Test') {
        workflowNote = [
          '',
          '=== TEST TASK WORKFLOW — READ CAREFULLY ===',
          '',
          'This is a Test task. You must run ACTUAL UI tests using agent-browser.',
          'You CANNOT just mark this task as Done — it requires a passing test execution.',
          '',
          'Steps:',
          '1. Read the test plan: check the task description and linked test plan for what to verify',
          '2. Start execution: call start_test_execution with the test plan ID',
          '3. Open the app: use agent-browser to open the application URL',
          '4. For EACH test step:',
          '   a. Perform the action described in the step using agent-browser',
          '   b. Verify the expected result visually',
          '   c. Call record_step_result with Passed or Failed',
          '5. Complete execution: call complete_test_execution with Passed or Failed',
          '6. If ALL steps passed: call complete_task to mark the Test task as Done',
          '7. If ANY step failed: call report_test_failure on the SOURCE task with details',
          '',
          'IMPORTANT:',
          '- complete_task WILL BE BLOCKED if you skip the test execution steps',
          '- Do NOT retry complete_task hoping it will work — run the tests first',
          '- The agent-browser skill is: agent-browser open <url>',
          '============================================',
        ].join('\n');
      } else if (['Feature', 'Bug', 'Refactor'].includes(taskType)) {
        workflowNote = [
          '',
          '=== MANDATORY WORKFLOW ===',
          '',
          '1. CODE: Implement with TDD — write tests FIRST, then implementation',
          '2. REVIEW: Call request_review with backendTests[] and testPlan',
          '   - All tests are registered on THIS task (no separate Test task)',
          '   - Include unit, integration, AND UI test scenarios',
          '3. TESTS MUST PASS: Task cannot complete until all registered tests pass',
          '4. PR: Create a PR after review submission',
          '',
          'WHEN WRITING TESTS:',
          '- Write tests BEFORE implementation (TDD)',
          '- Track every test file in backendTests:',
          '  backendTests: [',
          '    { name: "OrderServiceTests", testFile: "Tests/Services/OrderServiceTests.cs", type: "Unit", framework: "xUnit" },',
          '    { name: "InvoiceIntegrationTests", testFile: "Tests/Integration/InvoiceTests.cs", type: "Integration", framework: "xUnit" }',
          '  ]',
          '- Define UI test scenarios in testPlan parameter',
          '- Both go on THIS task — no separate test task',
          '',
          'IMPORTANT:',
          '- Do NOT call complete_task directly — use request_review first',
          '- complete_task is BLOCKED until all tests on this task pass',
          '========================',
        ].join('\n');
      }

      // Also fetch project-specific test commands if available
      let testingNote = '';
      try {
        const projectSettings = await api.get<ProjectSettings>(`/ai/settings?projectId=${getActiveProjectId()}`);
        const settings = projectSettings?.settings || {};
        const unitCmd = settings.unitTestCommand || 'dotnet test';
        const integrationCmd = settings.integrationTestCommand || 'dotnet test --filter Integration';
        testingNote = [
          '',
          '=== PROJECT TEST COMMANDS ===',
          `Unit tests: ${unitCmd}`,
          `Integration tests: ${integrationCmd}`,
          'Run these before calling request_review to catch issues early.',
          '=============================',
        ].join('\n');
      } catch {
        // Settings fetch failed, continue
      }

      return { content: [{ type: 'text' as const, text: JSON.stringify(result) + workflowNote + testingNote }] };
    }
  );

  server.tool(
    'complete_task',
    'Move task to Done. For Feature/Bug/Refactor tasks, the task must be in Review status (use request_review first). Test/Docs/Infra/Research tasks can be completed directly.',
    {
      taskId: z.number().describe('Task ID to complete'),
      commitSha: z.string().optional().describe('Git commit SHA'),
      gitBranch: z.string().optional().describe('Git branch name'),
      prUrl: z.string().optional().describe('Pull request URL'),
    },
    async ({ taskId, commitSha, gitBranch, prUrl }) => {
      setLastActivity(`Completing task #${taskId}`);

      // Fetch task to check type and status
      const REVIEW_REQUIRED_TYPES = ['Feature', 'Bug', 'Refactor'];
      try {
        const task = await api.get<{ id: number; type: string; status: string; title: string }>(
          `/tasks/${taskId}`
        );
        if (REVIEW_REQUIRED_TYPES.includes(task.type) && task.status !== 'Review') {
          const blockMsg = [
            '=== COMPLETION BLOCKED ===',
            '',
            `Task #${taskId} "${task.title}" is a ${task.type} task in ${task.status} status.`,
            '',
            'Feature/Bug/Refactor tasks must go through Review before completion.',
            'Use request_review to submit this task for review first.',
            '==========================',
          ].join('\n');
          return { content: [{ type: 'text' as const, text: blockMsg }] };
        }
      } catch {
        // If we can't fetch the task, let the API handle validation
      }

      // Check test enforcement - skip UI checks
      try {
        const canComplete = await api.get<{ canComplete: boolean; reason: string | null }>(
          `/tasks/${taskId}/can-complete`
        );
        if (!canComplete.canComplete) {
          // Give specific guidance based on what's blocking
          const reason = canComplete.reason || '';
          let guidance = '';
          if (reason.includes('passing test execution')) {
            guidance = [
              '',
              'STOP: Do NOT retry complete_task — it will keep failing.',
              'You must run UI tests with agent-browser first:',
              '',
              '  1. start_test_execution — begin the test plan execution',
              '  2. agent-browser open <app-url> — open the app in a browser',
              '  3. For each test step, verify it visually with agent-browser',
              '  4. record_step_result — record Passed/Failed for each step',
              '  5. complete_test_execution — mark execution as Passed or Failed',
              '  6. THEN call complete_task',
            ].join('\n');
          } else {
            guidance = '\nFix the issues above, then call complete_task again.';
          }
          const blockMsg = [
            '=== COMPLETION BLOCKED ===',
            '',
            canComplete.reason,
            guidance,
            '==========================',
          ].join('\n');
          return { content: [{ type: 'text' as const, text: blockMsg }] };
        }
      } catch (err) {
        const blockMsg = [
          '=== COMPLETION BLOCKED - TEST ENFORCEMENT UNAVAILABLE ===',
          '',
          'Could not reach the test enforcement endpoint.',
          `Error: ${err instanceof Error ? err.message : String(err)}`,
          '',
          'Ensure the lifecycle API is running and try again.',
          '=========================================================',
        ].join('\n');
        return { content: [{ type: 'text' as const, text: blockMsg }] };
      }

      // All checks passed — complete the task
      const result = await api.post(`/ai/tasks/${taskId}/transition`, {
        status: 'Done',
        gitCommitSha: commitSha,
        gitBranch,
        pullRequestUrl: prUrl,
      });

      return { content: [{ type: 'text' as const, text: JSON.stringify(result) }] };
    }
  );

  server.tool(
    'request_review',
    'Submit a task for review. Moves task to Review status. REQUIRES at least one test source: backendTests[] (unit/integration) and/or testPlan (UI scenarios). The API will reject requests with zero tests for Feature/Bug/Refactor tasks.',
    {
      taskId: z.number().describe('Task ID to submit for review'),
      commitSha: z.string().optional().describe('Git commit SHA'),
      gitBranch: z.string().optional().describe('Git branch name'),
      prUrl: z.string().optional().describe('Pull request URL'),
      testPlan: z.object({
        name: z.string(),
        testLevel: z.enum(['Smoke', 'Comprehensive', 'FullE2E']).default('Smoke'),
        tests: z.array(z.object({
          name: z.string(),
          type: z.enum(['Unit', 'Integration', 'UI', 'Manual']).default('UI'),
          steps: z.array(z.object({
            description: z.string(),
            expectedResult: z.string().optional(),
            stepType: z.enum(['Setup', 'Action', 'Assertion', 'Teardown']).default('Action'),
          }))
        }))
      }).optional().describe('UI test plan with steps for agent-browser testing. REQUIRED for tasks that affect the UI.'),
      backendTests: z.array(z.object({
        name: z.string().describe('Test class or describe block name'),
        testFile: z.string().describe('Relative path to test file from project root'),
        type: z.enum(['Unit', 'Integration']).default('Unit'),
        framework: z.string().optional().describe('Test framework: xUnit, Vitest, etc.'),
      })).optional().describe('Backend test files written during implementation. REQUIRED for Feature/Bug/Refactor tasks — provide at least backendTests OR testPlan.'),
    },
    async ({ taskId, commitSha, gitBranch, prUrl, testPlan, backendTests }) => {
      setLastActivity(`Requesting review for task #${taskId}`);
      const body: Record<string, unknown> = {};
      if (commitSha) body.gitCommitSha = commitSha;
      if (gitBranch) body.gitBranch = gitBranch;
      if (prUrl) body.pullRequestUrl = prUrl;
      if (testPlan) {
        body.testPlan = {
          testPlanName: testPlan.name,
          testLevel: testPlan.testLevel,
          tests: testPlan.tests.map(t => ({
            name: t.name,
            type: t.type,
            steps: t.steps.map(s => ({
              description: s.description,
              expectedResult: s.expectedResult,
              stepType: s.stepType,
            })),
          })),
        };
      }
      if (backendTests) body.backendTests = backendTests;
      const result = await api.post<{ id: number; backendTestPlanId?: number; backendTestCount?: number }>(`/ai/tasks/${taskId}/request-review`, body);

      let backendNote = '';
      if (result.backendTestPlanId) {
        backendNote = [
          '',
          '=== BACKEND TESTS REGISTERED ===',
          `${result.backendTestCount} backend test(s) registered on task #${result.id}.`,
          'Run your tests now to verify they pass before the review proceeds.',
          '================================',
        ].join('\n');
      }

      return { content: [{ type: 'text' as const, text: JSON.stringify(result) + backendNote }] };
    }
  );

  server.tool(
    'cancel_task',
    'Cancel a task that is no longer needed. Use when work was superseded, duplicated, or abandoned. Can cancel from any status except Done.',
    {
      taskId: z.number().describe('Task ID to cancel'),
      reason: z.string().describe('Why the task is being cancelled (e.g. "Superseded by task #174 in phase 23")'),
    },
    async ({ taskId, reason }) => {
      setLastActivity(`Cancelling task #${taskId}`);
      const result = await api.post(`/ai/tasks/${taskId}/transition`, { status: 'Cancelled' });

      // Add a comment with the cancellation reason
      try {
        await api.post(`/tasks/${taskId}/comments`, {
          content: `Cancelled: ${reason}`,
          source: 'System',
          author: 'Claude',
        });
      } catch {
        // Comment is best-effort
      }

      return { content: [{ type: 'text' as const, text: JSON.stringify(result) }] };
    }
  );

  server.tool(
    'report_test_failure',
    'Report a test failure back to the source task. Moves the source task back to InProgress with failure details.',
    {
      sourceTaskId: z.number().describe('The source (feature/bug) task ID to report failure on'),
      failureDescription: z.string().describe('Description of what failed and why'),
      testTaskId: z.number().optional().describe('The test task ID that found the failure'),
    },
    async ({ sourceTaskId, failureDescription, testTaskId }) => {
      setLastActivity(`Reporting test failure on task #${sourceTaskId}`);
      const result = await api.post(`/ai/tasks/${sourceTaskId}/report-test-failure`, {
        failureDescription,
        testTaskId,
      });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result) }] };
    }
  );
}
