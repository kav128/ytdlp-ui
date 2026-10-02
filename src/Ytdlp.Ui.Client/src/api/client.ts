export type LibraryFile = { id: string; name: string; size: number; lastModified: string; contentUrl: string };
export type Attempt = { number: number; state: string; createdAt: string; error: string | null };
export type Download = {
  id: string; url: string; title: string | null; state: string; resumeState: string | null;
  progress: number | null; createdAt: string; errorCode: string | null; error: string | null;
  allowedActions: string[]; attempts: Attempt[];
};
export type AccessToken = { accessToken: string; expiresAt: string; tokenType: 'Bearer' };

export class ApiError extends Error {
  constructor(public status: number, public code: string) { super(code); }
}

export async function request<T>(path: string, options: {
  method?: string; body?: unknown; token?: string | null; signal?: AbortSignal;
} = {}): Promise<T> {
  const headers = new Headers();
  if (options.body !== undefined) headers.set('Content-Type', 'application/json');
  if (options.token) headers.set('Authorization', `Bearer ${options.token}`);
  const response = await fetch(`/api${path}`, {
    method: options.method ?? 'GET', headers, signal: options.signal,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as { code?: string };
    throw new ApiError(response.status, problem.code ?? (response.status === 401 ? 'unauthorized' : 'request_failed'));
  }
  return response.status === 204 ? undefined as T : await response.json() as T;
}

export const api = {
  library: (signal?: AbortSignal) => request<LibraryFile[]>('/library', { signal }),
  login: (username: string, password: string) => request<AccessToken>('/auth/login', { method: 'POST', body: { username, password } }),
  session: (token: string, signal?: AbortSignal) => request<{ authenticated: boolean }>('/auth/session', { token, signal }),
  logout: (token: string) => request<void>('/auth/logout', { method: 'POST', token }),
  downloads: (token: string, signal?: AbortSignal) => request<Download[]>('/downloads', { token, signal }),
  create: (url: string, token: string) => request<Download>('/downloads', { method: 'POST', body: { url }, token }),
  act: (id: string, action: string, token: string) => request<Download>(`/downloads/${encodeURIComponent(id)}/${action}`, { method: 'POST', token }),
  deleteFile: (id: string, token: string) => request<void>(`/library/${encodeURIComponent(id)}`, { method: 'DELETE', token }),
};
