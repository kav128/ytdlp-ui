import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../../api/client';

export const sessionKey = 'ytdlp-ui.session';

function storedToken(): string | null {
  try {
    const saved = JSON.parse(sessionStorage.getItem(sessionKey) ?? 'null') as { accessToken?: unknown } | null;
    return typeof saved?.accessToken === 'string' ? saved.accessToken : null;
  } catch { sessionStorage.removeItem(sessionKey); return null; }
}

export function useAuth() {
  const [token, setToken] = useState(storedToken);
  const tokenRef = useRef(token);
  const [authenticatedToken, setAuthenticatedToken] = useState<string | null>(null);
  const [checking, setChecking] = useState(token !== null);
  const [expired, setExpired] = useState(false);

  const clear = useCallback((currentToken: string) => {
    if (tokenRef.current !== currentToken) return;
    tokenRef.current = null;
    sessionStorage.removeItem(sessionKey);
    setToken(null);
    setAuthenticatedToken(null);
    setChecking(false);
  }, []);

  const unauthorized = useCallback((error: unknown, currentToken: string) => {
    if (error instanceof ApiError && error.status === 401 && tokenRef.current === currentToken) {
      clear(currentToken);
      setExpired(true);
    }
  }, [clear]);

  useEffect(() => {
    if (!token) return;
    const controller = new AbortController();
    let active = true;
    setChecking(true);
    api.session(token, controller.signal).then(result => {
      if (!active || tokenRef.current !== token) return;
      if (result.authenticated) setAuthenticatedToken(token);
      else clear(token);
      setChecking(false);
    }).catch(error => {
      if (!active) return;
      unauthorized(error, token);
      setChecking(false);
    });
    return () => { active = false; controller.abort(); };
  }, [token, clear, unauthorized]);

  async function login(username: string, password: string) {
    const session = await api.login(username, password);
    sessionStorage.setItem(sessionKey, JSON.stringify(session));
    tokenRef.current = session.accessToken;
    setChecking(true);
    setExpired(false);
    setToken(session.accessToken);
  }

  async function logout() {
    const currentToken = tokenRef.current;
    if (!currentToken) return;
    try { await api.logout(currentToken); clear(currentToken); }
    catch (error) { unauthorized(error, currentToken); throw error; }
  }

  return { token: token && authenticatedToken === token ? token : null, checking, expired, login, logout, unauthorized };
}
