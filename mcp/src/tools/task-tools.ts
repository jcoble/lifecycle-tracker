import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActiveProjectId } from '../api-client.js';

interface ProjectSettings {
  id: number;
  settings: Record<string, string>;
}

interface TestRecord {
  id: number;
  taskId: number;
  testType: string;
  status: string;
  testName: string;
  lastRunAt: string | null;
  lastRunResult: string | null;
}

function buildTestingInstructions(settings: Record<string, string>, stage: 'write' | 'run'): string {
  const unitLevel = settings.unitTestLevel || 'Full';
  const integrationLevel = settings.integrationTestLevel || 'Full';
  const uiLevel = settings.uiTestLevel || 'Smoke';
  const unitCmd = settings.unitTestCommand || 'dotnet test';
  const integrationCmd = settings.integrationTestCommand || 'dotnet test --filter Integration';
  const webTool = settings.webTestTool || 'agent-browser';

  if (stage === 'write') {
    const lines = [
      '=== TESTING REQUIRED (enforced by lifecycle) ===',
      '',
      'You MUST write tests for this task before moving to Review.',
      'Use record_test to register each test, then record_test_result after running.',
      '',
      `1. UNIT TESTS (${unitLevel} coverage)`,
      `   Command: ${unitCmd}`,
      unitLevel === 'Full'
        ? '   Write tests for ALL methods/logic changed. 100% coverage of new code.'
        : '   Write tests for critical paths.',
      '',
      `2. INTEGRATION TESTS (${integrationLevel} coverage)`,
      `   Command: ${integrationCmd}`,
      integrationLevel === 'Full'
        ? '   Write tests for ALL API endpoints and DB operations affected. Use EdiPlatform_AutomatedTests DB.'
        : '   Write tests for main integration points.',
      '',
      `3. UI/E2E TESTS (${uiLevel})`,
      `   Tool: ${webTool}`,
      uiLevel === 'Smoke'
        ? '   Happy-path smoke tests only. Verify the feature works end-to-end in the browser.'
        : uiLevel === 'Full'
          ? '   Full E2E coverage including error states and edge cases.'
          : '   Skip UI tests for backend-only changes. Add smoke test if UI is affected.',
      '',
      'After writing tests, RUN them and record results before moving to Review.',
      'complete_task will BLOCK if tests are missing or not run.',
      '================================================',
    ];
    return lines.join('\n');
  } else {
    const lines = [
      '=== RUN ALL TESTS (enforced by lifecycle) ===',
      '',
      'Before completing this task, you MUST:',
      `1. Run unit tests: ${unitCmd}`,
      `2. Run integration tests: ${integrationCmd}`,
      `3. Run UI smoke tests with ${webTool} (if applicable)`,
      '',
      'Record results with record_test_result for each test.',
      'complete_task will BLOCK if any tests are unrun or failing.',
      '===============================================',
    ];
    return lines.join('\n');
  }
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
      const pid = projectId ?? getActiveProjectId();
      const result = await api.post('/ai/tasks/bulk-create', { projectId: pid, phaseId, tasks });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'start_task',
    'Move a task to InProgress status. Returns TESTING REQUIREMENTS that must be followed.',
    {
      taskId: z.number().describe('Task ID to start'),
    },
    async ({ taskId }) => {
      const result = await api.post(`/ai/tasks/${taskId}/transition`, { status: 'InProgress' });

      // Fetch project settings and include testing instructions
      let testingInstructions = '';
      try {
        const projectSettings = await api.get<ProjectSettings>('/ai/settings');
        if (projectSettings?.settings?.testEnforcement === 'true') {
          testingInstructions = '\n\n' + buildTestingInstructions(projectSettings.settings, 'write');
        }
      } catch {
        // Settings fetch failed, continue without instructions
      }

      // Check if task has RequiredTestLevel for UI test plan requirements
      let testPlanInstructions = '';
      try {
        const taskDetail = result as Record<string, unknown>;
        const requiredTestLevel = taskDetail?.requiredTestLevel;
        if (requiredTestLevel) {
          testPlanInstructions = [
            '',
            '=== UI TEST PLAN REQUIRED (enforced by lifecycle) ===',
            '',
            `This task requires a UI test plan at level: ${requiredTestLevel}`,
            '',
            'You MUST create and execute a test plan before completing this task:',
            '1. Create test plan: create_test_plan (with steps for agent-browser)',
            '2. Start execution: start_test_execution',
            '3. Run each step with agent-browser and record: record_step_result',
            '4. Complete execution: complete_test_execution',
            '',
            'complete_task will BLOCK until a test plan has a passing execution.',
            '=====================================================',
          ].join('\n');
        }
      } catch {
        // Task detail parsing failed, continue
      }

      const output = JSON.stringify(result, null, 2) + testingInstructions + testPlanInstructions;
      return { content: [{ type: 'text' as const, text: output }] };
    }
  );

  server.tool(
    'complete_task',
    'Move a task to Done status. BLOCKS if required tests are missing or not run.',
    {
      taskId: z.number().describe('Task ID to complete'),
      commitSha: z.string().optional().describe('Git commit SHA'),
      gitBranch: z.string().optional().describe('Git branch name'),
      prUrl: z.string().optional().describe('Pull request URL'),
    },
    async ({ taskId, commitSha, gitBranch, prUrl }) => {
      // Check test enforcement BEFORE completing
      let enforcement = false;
      try {
        const projectSettings = await api.get<ProjectSettings>('/ai/settings');
        enforcement = projectSettings?.settings?.testEnforcement === 'true';
      } catch {
        // Continue without enforcement if settings unavailable
      }

      if (enforcement) {
        // Fetch test records for this task
        let tests: TestRecord[] = [];
        try {
          tests = await api.get<TestRecord[]>(`/ai/tasks/${taskId}/tests`);
        } catch {
          // If endpoint doesn't exist yet, skip check
        }

        const hasUnit = tests.some(t => t.testType === 'Unit');
        const hasIntegration = tests.some(t => t.testType === 'Integration');
        const allRun = tests.length > 0 && tests.every(t => t.lastRunAt !== null);
        const anyFailing = tests.some(t => t.status === 'Failing');

        const issues: string[] = [];
        if (tests.length === 0) {
          issues.push('NO TESTS RECORDED. You must write tests and register them with record_test before completing.');
        } else {
          if (!hasUnit) issues.push('No UNIT test recorded. Write unit tests and register with record_test.');
          if (!hasIntegration) issues.push('No INTEGRATION test recorded. Write integration tests and register with record_test.');
          if (!allRun) issues.push('Some tests have NOT BEEN RUN. Execute tests and record results with record_test_result.');
          if (anyFailing) issues.push('Some tests are FAILING. Fix failures before completing.');
        }

        // Check UI test plan requirements via can-complete endpoint
        try {
          const canComplete = await api.get<{ canComplete: boolean; reason: string | null }>(
            `/tasks/${taskId}/can-complete`
          );
          if (!canComplete.canComplete) {
            issues.push(`UI TEST PLAN BLOCKED: ${canComplete.reason}. Create a test plan with create_test_plan, run it with agent-browser, and record results.`);
          }
        } catch {
          // Endpoint not available, skip test plan check
        }

        if (issues.length > 0) {
          const blockMsg = [
            '=== COMPLETION BLOCKED BY LIFECYCLE TEST ENFORCEMENT ===',
            '',
            ...issues,
            '',
            `Tests found: ${tests.length} (Unit: ${tests.filter(t => t.testType === 'Unit').length}, Integration: ${tests.filter(t => t.testType === 'Integration').length}, E2E: ${tests.filter(t => t.testType === 'EndToEnd').length})`,
            `Tests run: ${tests.filter(t => t.lastRunAt).length}/${tests.length}`,
            `Tests passing: ${tests.filter(t => t.status === 'Passing').length}/${tests.length}`,
            '',
            'Write and run the missing tests, then call complete_task again.',
            '=========================================================',
          ].join('\n');

          return { content: [{ type: 'text' as const, text: blockMsg }] };
        }
      }

      // All checks passed — complete the task
      const result = await api.post(`/ai/tasks/${taskId}/transition`, {
        status: 'Done',
        gitCommitSha: commitSha,
        gitBranch,
        pullRequestUrl: prUrl,
      });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );
}
