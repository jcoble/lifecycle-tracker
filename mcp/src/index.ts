import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { registerProjectTools } from './tools/project-tools.js';
import { registerTaskTools } from './tools/task-tools.js';
import { registerPhaseTools } from './tools/phase-tools.js';
import { registerContextTools } from './tools/context-tools.js';
import { registerTeamTools } from './tools/team-tools.js';
import { registerMilestoneTools } from './tools/milestone-tools.js';
import { registerTestPlanTools } from './tools/test-plan-tools.js';
import { api } from './api-client.js';

const AGENT_NAME = process.env.LIFECYCLE_AGENT_NAME || '';
const AGENT_MODEL = process.env.LIFECYCLE_AGENT_MODEL || '';
const AGENT_ROLE = process.env.LIFECYCLE_AGENT_ROLE || '';
const SESSION_ID = `${process.pid}-${Date.now().toString(36)}`;
const HEARTBEAT_INTERVAL = 15_000; // 15 seconds

let registeredTeamMemberId: number | null = null;
let heartbeatTimer: ReturnType<typeof setInterval> | null = null;
let lastActivity: string | null = null;

/** Track the latest activity text — called by tool wrappers. Sends heartbeat immediately. */
export function setLastActivity(activity: string) {
  lastActivity = activity;
  // Fire heartbeat immediately so monitor updates right away
  sendHeartbeat();
}

export function getTeamMemberId(): number | null {
  return registeredTeamMemberId;
}

async function autoRegister() {
  if (!AGENT_NAME) return;
  try {
    const result = await api.post<{ teamMemberId: number; sessionId: string }>('/teams/auto-register', {
      agentName: AGENT_NAME,
      modelName: AGENT_MODEL || undefined,
      role: AGENT_ROLE || undefined,
      sessionId: SESSION_ID,
    });
    registeredTeamMemberId = result.teamMemberId;
  } catch {
    // API may not be running yet — don't block MCP startup
  }
}

async function sendHeartbeat() {
  if (!registeredTeamMemberId) return;
  try {
    await api.post(`/teams/${registeredTeamMemberId}/heartbeat`, {
      activity: lastActivity,
    });
  } catch {
    // Heartbeat failure is non-fatal
  }
}

function startHeartbeat() {
  if (!registeredTeamMemberId) return;
  // Send initial heartbeat immediately
  sendHeartbeat();
  // Then every HEARTBEAT_INTERVAL
  heartbeatTimer = setInterval(sendHeartbeat, HEARTBEAT_INTERVAL);
}

function stopHeartbeat() {
  if (heartbeatTimer) {
    clearInterval(heartbeatTimer);
    heartbeatTimer = null;
  }
}

async function autoShutdown() {
  stopHeartbeat();
  if (!registeredTeamMemberId) return;
  try {
    await api.post(`/teams/${registeredTeamMemberId}/shutdown`, {});
  } catch {
    // Best-effort cleanup
  }
}

const server = new McpServer({
  name: 'lifecycle',
  version: '1.0.0',
});

registerProjectTools(server);
registerTaskTools(server);
registerPhaseTools(server);
registerTestPlanTools(server);
registerContextTools(server);
registerTeamTools(server);
registerMilestoneTools(server);

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);

  // Auto-register after MCP connection is established
  await autoRegister();

  // Start automatic heartbeat
  startHeartbeat();

  // Graceful shutdown on process exit
  process.on('SIGINT', async () => { await autoShutdown(); process.exit(0); });
  process.on('SIGTERM', async () => { await autoShutdown(); process.exit(0); });
  process.on('beforeExit', async () => { await autoShutdown(); });
}

main().catch(console.error);
