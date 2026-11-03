import {useEffect, useState} from 'react';
import {api, type Download} from '../../api/client';
import type {AuthenticatedRequest} from '../auth/useAuth';

export function useDownloads(token: string | null, revision: number, authorized: AuthenticatedRequest, unauthorized: (error: unknown, token: string) => void) {
    const [result, setResult] = useState<{
        token: string;
        data: Download[];
        loading: boolean;
        error: boolean
    } | null>(null);
    useEffect(() => {
        if (!token) {
            setResult(null);
            return;
        }
        const controller = new AbortController();
        let active = true, running = false;
        let timer: ReturnType<typeof setTimeout> | undefined;

        async function poll() {
            if (!active || running || document.hidden) return;
            running = true;
            setResult(previous => ({
                token: token!, data: previous?.token === token ? previous.data : [],
                error: previous?.token === token && previous.error, loading: true
            }));
            try {
                const data = await authorized(() => api.downloads(controller.signal), token!);
                if (active) setResult({token: token!, data, error: false, loading: false});
            } catch (error) {
                if (active) {
                    unauthorized(error, token!);
                    setResult(previous => ({
                        token: token!,
                        data: previous?.token === token ? previous.data : [],
                        error: true,
                        loading: false
                    }));
                }
            } finally {
                running = false;
                if (active) timer = setTimeout(poll, 5000);
            }
        }

        function refresh() {
            clearTimeout(timer);
            if (!document.hidden) void poll();
        }

        void poll();
        window.addEventListener('focus', refresh);
        document.addEventListener('visibilitychange', refresh);
        return () => {
            active = false;
            controller.abort();
            clearTimeout(timer);
            window.removeEventListener('focus', refresh);
            document.removeEventListener('visibilitychange', refresh);
        };
    }, [token, revision, authorized, unauthorized]);
    const current = token !== null && result?.token === token;
    return {
        downloads: current ? result.data : [],
        loading: token !== null && (!current || result.loading),
        error: current && result.error
    };
}
