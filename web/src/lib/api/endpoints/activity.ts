import type { ActivityLog } from '$lib/types';
import { api } from '../client';

export const activity = {
	list: (projectId: number, params?: Record<string, string>) => {
		const query = params ? '?' + new URLSearchParams(params).toString() : '';
		return api.get<ActivityLog[]>(`/projects/${projectId}/activity${query}`);
	},
};
