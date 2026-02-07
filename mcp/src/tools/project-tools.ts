import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api } from '../api-client.js';

export function registerProjectTools(server: McpServer) {
  server.tool(
    'get_project_context',
    'Load full project state including active milestone, phases, tasks, and recent activity',
    {},
    async () => {
      const context = await api.get('/ai/context');
      return { content: [{ type: 'text' as const, text: JSON.stringify(context, null, 2) }] };
    }
  );

  server.tool(
    'search_tasks',
    'Search tasks by title/description, optionally filtered by status, priority, or phase',
    {
      query: z.string().optional().describe('Search query for title/description'),
      status: z.string().optional().describe('Filter by status: Backlog, Todo, InProgress, Review, Blocked, Done'),
      priority: z.string().optional().describe('Filter by priority: P1, P2, P3, P4'),
      phaseId: z.number().optional().describe('Filter by phase ID'),
    },
    async ({ query, status, priority, phaseId }) => {
      const params = new URLSearchParams();
      if (query) params.set('search', query);
      if (status) params.set('status', status);
      if (priority) params.set('priority', priority);
      if (phaseId) params.set('phaseId', phaseId.toString());
      const tasks = await api.get(`/tasks?${params}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(tasks, null, 2) }] };
    }
  );

  server.tool(
    'get_activity',
    'Get recent activity feed for the project',
    {
      projectId: z.number().default(1).describe('Project ID'),
      limit: z.number().default(20).describe('Number of activity items to return'),
    },
    async ({ projectId, limit }) => {
      const activity = await api.get(`/projects/${projectId}/activity?pageSize=${limit}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(activity, null, 2) }] };
    }
  );

  server.tool(
    'get_metrics',
    'Get project metrics summary including task counts, test stats, and phase progress',
    {
      projectId: z.number().default(1).describe('Project ID'),
    },
    async ({ projectId }) => {
      const dashboard = await api.get(`/projects/${projectId}/dashboard`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(dashboard, null, 2) }] };
    }
  );
}
