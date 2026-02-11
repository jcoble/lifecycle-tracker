import { writable } from 'svelte/store';

// Tracks which task IDs have an active test agent (spawned via spawn-test / message-agent)
export const activeTestAgentTasks = writable<Set<number>>(new Set());

export function markTestAgentActive(taskId: number) {
	activeTestAgentTasks.update((s) => {
		s.add(taskId);
		return new Set(s);
	});
}

export function markTestAgentStopped(taskId: number) {
	activeTestAgentTasks.update((s) => {
		s.delete(taskId);
		return new Set(s);
	});
}
