import type { TestRecord } from '$lib/types';
import { api } from '../client';

export const tests = {
	list: (taskId: number) => api.get<TestRecord[]>(`/tasks/${taskId}/tests`),
	create: (taskId: number, data: Partial<TestRecord>) => api.post<TestRecord>(`/tasks/${taskId}/tests`, data),
	update: (id: number, data: Partial<TestRecord>) => api.patch<TestRecord>(`/tests/${id}`, data),
	delete: (id: number) => api.delete(`/tests/${id}`),
};
