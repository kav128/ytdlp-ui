import { useEffect, useState } from 'react';
import { api, type Download } from '../../api/client';

export function useDownloads(token: string | null, revision: number, unauthorized: (error: unknown, token: string) => void) {
  const [result, setResult] = useState<{ token: string; data: Download[] } | null>(null);
  const [error, setError] = useState(false);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!token) { setResult(null); setError(false); return; }
    const controller = new AbortController();
    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let running = false;
    async function poll() {
      if (!active || running || document.hidden) return;
      running = true;
      setLoading(true);
      try {
        const data = await api.downloads(token!, controller.signal);
        if (active) { setResult({ token: token!, data }); setError(false); }
      } catch (error) {
        if (active) { unauthorized(error, token!); setError(true); }
      } finally {
        running = false;
        if (active) {
          setLoading(false);
          timer = setTimeout(poll, 5000);
        }
      }
    }
    function onVisibility() { clearTimeout(timer); if (!document.hidden) void poll(); }
    void poll();
    document.addEventListener('visibilitychange', onVisibility);
    return () => { active = false; controller.abort(); clearTimeout(timer); document.removeEventListener('visibilitychange', onVisibility); };
  }, [token, revision, unauthorized]);

  return { downloads: result?.token === token ? result.data : [], loading, error };
}
