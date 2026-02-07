import type { Milestone } from '$lib/types';
import { api } from '../client';

export const milestones = {
	list: (projectId: number) => api.get<Milestone[]>(`/projects/${projectId}/milestones`),
	get: (id: number) => api.get<Milestone>(`/milestones/${id}`),
	create: (data: Partial<Milestone>) => api.post<Milestone>('/milestones', data),
	update: (id: number, data: Partial<Milestone>) => api.patch<Milestone>(`/milestones/${id}`, data),
	delete: (id: number) => api.delete(`/milestones/${id}`),
};
