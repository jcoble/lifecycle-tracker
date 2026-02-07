const API_URL = process.env.LIFECYCLE_API_URL || 'http://localhost:5556';
const API_KEY = process.env.LIFECYCLE_API_KEY || '';

export async function apiRequest<T>(path: string, options: RequestInit = {}): Promise<T> {
  const res = await fetch(`${API_URL}/api${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      'X-API-Key': API_KEY,
      ...options.headers,
    },
  });
  if (res.status === 204) return undefined as T;
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`API error ${res.status}: ${text}`);
  }
  return res.json();
}

export const api = {
  get: <T>(path: string) => apiRequest<T>(path),
  post: <T>(path: string, data?: unknown) => apiRequest<T>(path, { method: 'POST', body: data ? JSON.stringify(data) : undefined }),
  patch: <T>(path: string, data: unknown) => apiRequest<T>(path, { method: 'PATCH', body: JSON.stringify(data) }),
  delete: (path: string) => apiRequest(path, { method: 'DELETE' }),
};
