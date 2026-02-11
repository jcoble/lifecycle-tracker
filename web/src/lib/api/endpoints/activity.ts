import type { ActivityLog } from '$lib/types';
import { api } from '../client';

export const activity = {
	list: async (projectId: number, params?: Record<string, string>): Promise<ActivityLog[]> => {
		const query = params ? '?' + new URLSearchParams(params).toString() : '';
		const result = await api.get<{ items: ActivityLog[] } | ActivityLog[]>(`/projects/${projectId}/activity${query}`);
		return Array.isArray(result) ? result : result.items;
	},
};
