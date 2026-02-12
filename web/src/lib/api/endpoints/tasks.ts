import type { Task } from '$lib/types';
import { api } from '../client';

export const tasks = {
	list: (params?: Record<string, string>) => {
		const query = params ? '?' + new URLSearchParams(params).toString() : '';
		return api.get<Task[]>(`/tasks${query}`);
	},
	get: (id: number) => api.get<Task>(`/tasks/${id}`),
	create: (data: Partial<Task>) => api.post<Task>('/tasks', data),
	update: (id: number, data: Partial<Task>) => api.patch<Task>(`/tasks/${id}`, data),
	delete: (id: number) => api.delete(`/tasks/${id}`),
	move: (id: number, status: string, orderInColumn: number) =>
		api.post<Task>(`/tasks/${id}/move`, { status, orderInColumn }),
	reorder: (status: string, items: { id: number; order: number }[]) =>
		api.patch('/tasks/reorder', { status, items }),
	archiveCompleted: (data: { projectId?: number; olderThanDays?: number; completedPhasesOnly?: boolean }) =>
		api.post<{ archivedCount: number }>('/tasks/archive-completed', data),
	spawnTestAgent: (taskId: number) =>
		api.post<{ message: string; taskId: number; logFile: string }>(`/tasks/${taskId}/spawn-test`, {}),
	getAgentLog: (taskId: number, offset = 0) =>
		api.get<{ lines: string[]; totalLines: number; running: boolean; sessionId: string | null }>(
			`/tasks/${taskId}/agent-log?offset=${offset}`
		),
	messageAgent: (taskId: number, message: string) =>
		api.post<{ message: string; taskId: number; sessionId: string }>(
			`/tasks/${taskId}/message-agent`,
			{ message }
		),
	stopAgent: (taskId: number) =>
		api.post<{ message: string }>(`/tasks/${taskId}/stop-agent`, {}),
	resolveTask: (taskId: number) =>
		api.post<{ message: string; taskId: number; logFile: string }>(`/tasks/${taskId}/resolve`, {}),
};
