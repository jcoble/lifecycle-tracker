import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api } from '../api-client.js';

export function registerTestTools(server: McpServer) {
  server.tool(
    'record_test',
    'Record that a test file was created for a task',
    {
      taskId: z.number().describe('Task ID'),
      testType: z.enum(['Unit', 'Integration', 'EndToEnd', 'Manual']).describe('Type of test'),
      testName: z.string().describe('Test name or description'),
      testFile: z.string().optional().describe('Path to test file'),
      framework: z.string().optional().describe('Test framework (e.g., xUnit, Playwright)'),
    },
    async (args) => {
      const result = await api.post('/ai/tests/record', args);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'record_test_result',
    'Record the result of running tests',
    {
      testId: z.number().describe('Test record ID'),
      passed: z.boolean().describe('Whether the test passed'),
      output: z.string().optional().describe('Test output/log'),
    },
    async (args) => {
      const result = await api.post('/ai/tests/result', args);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );
}
