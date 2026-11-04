import {useEffect, useState} from 'react';
import {api, type LibraryFile} from '../../api/client';

export function useLibrary(revision: string) {
    const [files, setFiles] = useState<LibraryFile[]>([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState(false);
    useEffect(() => {
        const controller = new AbortController();
        let active = true, running = false;
        let timer: ReturnType<typeof setTimeout> | undefined;

        async function load() {
            if (!active || running || document.hidden) return;
            running = true;
            setLoading(true);
            try {
                const result = await api.library(controller.signal);
                if (active) {
                    setFiles(result);
                    setError(false);
                }
            } catch {
                if (active) setError(true);
            } finally {
                running = false;
                if (active) {
                    setLoading(false);
                    timer = setTimeout(load, 5000);
                }
            }
        }

        function refresh() {
            clearTimeout(timer);
            if (!document.hidden) void load();
        }

        void load();
        window.addEventListener('focus', refresh);
        document.addEventListener('visibilitychange', refresh);
        return () => {
            active = false;
            controller.abort();
            clearTimeout(timer);
            window.removeEventListener('focus', refresh);
            document.removeEventListener('visibilitychange', refresh);
        };
    }, [revision]);
    return {files, loading, error};
}
