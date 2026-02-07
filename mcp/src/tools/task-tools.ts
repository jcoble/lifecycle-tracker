import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActiveProjectId } from '../api-client.js';

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
    'Move a task to InProgress status',
    {
      taskId: z.number().describe('Task ID to start'),
    },
    async ({ taskId }) => {
      const result = await api.post(`/ai/tasks/${taskId}/transition`, { status: 'InProgress' });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'complete_task',
    'Move a task to Done status with optional commit hash',
    {
      taskId: z.number().describe('Task ID to complete'),
      commitSha: z.string().optional().describe('Git commit SHA'),
      gitBranch: z.string().optional().describe('Git branch name'),
      prUrl: z.string().optional().describe('Pull request URL'),
    },
    async ({ taskId, commitSha, gitBranch, prUrl }) => {
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
