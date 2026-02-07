import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api } from '../api-client.js';

export function registerPhaseTools(server: McpServer) {
  server.tool(
    'create_phase',
    'Create a new phase within a milestone',
    {
      milestoneId: z.number().describe('Milestone ID'),
      name: z.string().describe('Phase name'),
      description: z.string().optional(),
      goal: z.string().optional().describe('Phase goal'),
      successCriteria: z.string().optional().describe('Success criteria in markdown'),
      phaseNumber: z.number().describe('Phase number for ordering'),
    },
    async ({ milestoneId, ...data }) => {
      const result = await api.post(`/milestones/${milestoneId}/phases`, data);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'breakdown_phase',
    'Create a phase and all its tasks in one call',
    {
      milestoneId: z.number().describe('Milestone ID'),
      phase: z.object({
        name: z.string(),
        description: z.string().optional(),
        goal: z.string().optional(),
        successCriteria: z.string().optional(),
        phaseNumber: z.number(),
      }),
      tasks: z.array(z.object({
        title: z.string(),
        description: z.string().optional(),
        priority: z.enum(['P1', 'P2', 'P3', 'P4']).default('P3'),
        type: z.enum(['Feature', 'Bug', 'Refactor', 'Docs', 'Test', 'Infra', 'Research']).default('Feature'),
      })),
    },
    async ({ milestoneId, phase, tasks }) => {
      const result = await api.post('/ai/phases/breakdown', { milestoneId, phase, tasks });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );
}
