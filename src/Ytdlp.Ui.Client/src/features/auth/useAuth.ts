import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError, type AccessToken } from '../../api/client';

export const sessionKey = 'ytdlp-ui.session';
export type AuthenticatedRequest = <T>(operation: (token: string) => Promise<T>, sessionId: string) => Promise<T>;

function storedSession(): AccessToken | null {
  try {
    const saved = JSON.parse(sessionStorage.getItem(sessionKey) ?? 'null') as Partial<AccessToken> | null;
    if (typeof saved?.accessToken === 'string' && typeof saved.refreshToken === 'string' && typeof saved.expiresAt === 'string') return saved as AccessToken;
    sessionStorage.removeItem(sessionKey);
  } catch { sessionStorage.removeItem(sessionKey); }
  return null;
}

export function useAuth() {
  const [initialSession] = useState(storedSession);
  // The initial access token identifies this UI session even when its token pair rotates.
  const [sessionId, setSessionId] = useState(initialSession?.accessToken ?? null);
  const sessionIdRef = useRef(sessionId);
  const sessionRef = useRef(initialSession);
  const refreshFlight = useRef<{ refreshToken: string; promise: Promise<AccessToken> } | null>(null);
  const [authenticatedSession, setAuthenticatedSession] = useState<string | null>(null);
  const [checking, setChecking] = useState(sessionId !== null);
  const [expired, setExpired] = useState(false);

  const clear = useCallback((currentSession: string) => {
    if (sessionIdRef.current !== currentSession) return;
    sessionIdRef.current = null;
    sessionRef.current = null;
    sessionStorage.removeItem(sessionKey);
    setSessionId(null);
    setAuthenticatedSession(null);
    setChecking(false);
  }, []);

  const unauthorized = useCallback((error: unknown, currentSession: string) => {
    if (error instanceof ApiError && error.status === 401 && sessionIdRef.current === currentSession) {
      clear(currentSession);
      setExpired(true);
    }
  }, [clear]);

  const authorized: AuthenticatedRequest = useCallback(async (operation, currentSession) => {
    function current() {
      if (sessionIdRef.current !== currentSession || !sessionRef.current) throw new DOMException('Session changed.', 'AbortError');
      return sessionRef.current;
    }
    async function refresh() {
      const previous = current();
      let flight = refreshFlight.current;
      if (flight?.refreshToken !== previous.refreshToken) {
        const promise = api.refresh(previous.refreshToken).then(next => {
          if (current().refreshToken !== previous.refreshToken) throw new DOMException('Session changed.', 'AbortError');
          sessionStorage.setItem(sessionKey, JSON.stringify(next));
          sessionRef.current = next;
          return next;
        }).finally(() => { if (refreshFlight.current?.promise === promise) refreshFlight.current = null; });
        flight = { refreshToken: previous.refreshToken, promise };
        refreshFlight.current = flight;
      }
      return flight.promise;
    }
    try {
      let session = current();
      if (Date.parse(session.expiresAt) <= Date.now()) session = await refresh();
      const usedToken = session.accessToken;
      try {
        const result = await operation(usedToken);
        current();
        return result;
      } catch (error) {
        if (!(error instanceof ApiError) || error.status !== 401) throw error;
        session = current();
        if (session.accessToken === usedToken) session = await refresh();
        const result = await operation(session.accessToken);
        current();
        return result;
      }
    } catch (error) { unauthorized(error, currentSession); throw error; }
  }, [unauthorized]);

  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();
    let active = true;
    setChecking(true);
    authorized(token => api.session(token, controller.signal), sessionId).then(result => {
      if (!active || sessionIdRef.current !== sessionId) return;
      if (result.authenticated) setAuthenticatedSession(sessionId);
      else clear(sessionId);
      setChecking(false);
    }).catch(() => { if (active) setChecking(false); });
    return () => { active = false; controller.abort(); };
  }, [sessionId, clear, authorized]);

  async function login(username: string, password: string) {
    const session = await api.login(username, password);
    sessionStorage.setItem(sessionKey, JSON.stringify(session));
    sessionRef.current = session;
    sessionIdRef.current = session.accessToken;
    setChecking(true);
    setExpired(false);
    setSessionId(session.accessToken);
  }

  async function logout() {
    const currentSession = sessionIdRef.current;
    if (!currentSession) return;
    await authorized(token => api.logout(token), currentSession);
    clear(currentSession);
  }

  return { sessionId: sessionId && authenticatedSession === sessionId ? sessionId : null, checking, expired, login, logout, unauthorized, authorized };
}
