import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { registerProjectTools } from './tools/project-tools.js';
import { registerTaskTools } from './tools/task-tools.js';
import { registerPhaseTools } from './tools/phase-tools.js';
import { registerTestTools } from './tools/test-tools.js';
import { registerContextTools } from './tools/context-tools.js';

const server = new McpServer({
  name: 'lifecycle',
  version: '1.0.0',
});

registerProjectTools(server);
registerTaskTools(server);
registerPhaseTools(server);
registerTestTools(server);
registerContextTools(server);

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch(console.error);
