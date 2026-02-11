import { writable, derived } from 'svelte/store';

export interface AgentActivityEntry {
	teamMemberId: number;
	agentName: string;
	role: string;
	status: 'active' | 'idle' | 'stale';
	currentActivity: string | null;
	taskId: number | null;
	taskTitle: string | null;
	sessionSpawnedAt: string | null;
	lastHeartbeat: string | null;
	latestPlanFileName: string | null;
	latestPlanUpdatedAt: string | null;
	hasPlan: boolean;
	isStale: boolean;
	modelName: string | null;
	tokensUsed: number | null;
}

export const agentActivityMap = writable<Map<number, AgentActivityEntry>>(new Map());

export const activeAgents = derived(agentActivityMap, ($map) =>
	Array.from($map.values())
		.filter((a) => a.status === 'active')
		.sort((a, b) => (a.sessionSpawnedAt || '').localeCompare(b.sessionSpawnedAt || ''))
);

export const activeAgentCount = derived(activeAgents, ($agents) => $agents.length);

// All agents (active + recently idle/stale) for the monitor page
export const allAgents = derived(agentActivityMap, ($map) =>
	Array.from($map.values())
		.sort((a, b) => {
			// Active first, then stale, then idle
			const order = { active: 0, stale: 1, idle: 2 };
			return (order[a.status] ?? 2) - (order[b.status] ?? 2);
		})
);

// Lookup: taskId -> assigned agent (for board cards)
export const taskAgentMap = derived(agentActivityMap, ($map) => {
	const result = new Map<number, AgentActivityEntry>();
	for (const entry of $map.values()) {
		if (entry.taskId && entry.status === 'active') {
			result.set(entry.taskId, entry);
		}
	}
	return result;
});
