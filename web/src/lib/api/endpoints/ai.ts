import { api } from '../client';

export const ai = {
	suggest: (taskId: number) => api.post<{ suggestions: string[] }>(`/tasks/${taskId}/ai/suggest`),
	decompose: (taskId: number) => api.post<{ subtasks: { title: string; description: string }[] }>(`/tasks/${taskId}/ai/decompose`),
};
