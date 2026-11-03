export type LibraryFile = { id: string; name: string; size: number; lastModified: string; contentUrl: string };
export type Attempt = { number: number; state: string; createdAt: string; error: string | null };
export type Download = {
    id: string; url: string; title: string | null; state: string; resumeState: string | null;
    progress: number | null; createdAt: string; errorCode: string | null; error: string | null;
    allowedActions: string[]; attempts: Attempt[];
};
export type Session = { authenticated: boolean; sessionId: string | null };

export class ApiError extends Error {
    constructor(public status: number, public code: string) {
        super(code);
    }
}

export async function request<T>(path: string, options: {
    method?: string; body?: unknown; csrf?: string; signal?: AbortSignal;
} = {}): Promise<T> {
    const headers = new Headers();
    if (options.body !== undefined && !(options.body instanceof FormData)) headers.set('Content-Type', 'application/json');
    if (options.csrf) headers.set('X-CSRF-TOKEN', options.csrf);
    const response = await fetch(`/api${path}`, {
        method: options.method ?? 'GET', headers, signal: options.signal, credentials: 'same-origin',
        body: options.body === undefined ? undefined : options.body instanceof FormData ? options.body : JSON.stringify(options.body),
    });
    if (!response.ok) {
        const problem = await response.json().catch(() => ({})) as { code?: string };
        throw new ApiError(response.status, problem.code ?? (response.status === 401 ? 'unauthorized' : 'request_failed'));
    }
    return response.status === 204 ? undefined as T : await response.json() as T;
}

export const api = {
    library: (signal?: AbortSignal) => request<LibraryFile[]>('/library', {signal}),
    upload: (file: File, csrf: string, signal?: AbortSignal) => {
        const form = new FormData();
        form.append('file', file);
        return request<LibraryFile>('/library/uploads', {method: 'POST', body: form, csrf, signal});
    },
    login: () => window.location.assign('/api/auth/login?returnUrl=%2F'),
    session: (signal?: AbortSignal) => request<Session>('/auth/session', {signal}),
    csrf: () => request<{ requestToken: string }>('/auth/csrf'),
    logout: (csrf: string) => request<void>('/auth/logout', {method: 'POST', csrf}),
    downloads: (signal?: AbortSignal) => request<Download[]>('/downloads', {signal}),
    create: (url: string, csrf: string) => request<Download>('/downloads', {method: 'POST', body: {url}, csrf}),
    act: (id: string, action: string, csrf: string) => request<Download>(`/downloads/${encodeURIComponent(id)}/${action}`, {
        method: 'POST',
        csrf
    }),
    deleteFile: (id: string, csrf: string) => request<void>(`/library/${encodeURIComponent(id)}`, {
        method: 'DELETE',
        csrf
    }),
};
