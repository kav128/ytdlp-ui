import {useCallback, useEffect, useRef, useState} from 'react';
import {api, ApiError} from '../../api/client';

export const sessionKey = 'ytdlp-ui.session';
export type AuthenticatedRequest = <T>(operation: (csrf: string) => Promise<T>, sessionId: string) => Promise<T>;

export function useAuth() {
    const [sessionId, setSessionId] = useState<string | null>(null);
    const sessionRef = useRef<string | null>(null);
    const generation = useRef(0);
    const verification = useRef(0);
    const mounted = useRef(false);
    const csrfFlight = useRef<{ sessionId: string; promise: Promise<string> } | null>(null);
    const [checking, setChecking] = useState(true);
    const [expired, setExpired] = useState(false);
    const [loginError, setLoginError] = useState(() => {
        const code = new URLSearchParams(window.location.search).get('authError');
        return code === 'access_denied' || code === 'login_failed' ? code : null;
    });

    const clear = useCallback((currentSession: string) => {
        if (sessionRef.current !== currentSession) return;
        generation.current++;
        sessionRef.current = null;
        csrfFlight.current = null;
        setSessionId(null);
        setChecking(false);
    }, []);

    const unauthorized = useCallback((error: unknown, currentSession: string) => {
        if (error instanceof ApiError && error.status === 401 && sessionRef.current === currentSession) {
            clear(currentSession);
            setExpired(true);
        }
    }, [clear]);

    const checkSession = useCallback(async (signal?: AbortSignal) => {
        const startedAt = generation.current;
        const attempt = ++verification.current;
        try {
            const next = await api.session(signal);
            if (!mounted.current || signal?.aborted || startedAt !== generation.current || attempt !== verification.current) return;
            const nextId = next.authenticated && typeof next.sessionId === 'string' ? next.sessionId : null;
            if (nextId) {
                setExpired(false);
                setLoginError(null);
            }
            if (nextId !== sessionRef.current) {
                if (!nextId && sessionRef.current) setExpired(true);
                generation.current++;
                sessionRef.current = nextId;
                csrfFlight.current = null;
                setSessionId(nextId);
            }
            setChecking(false);
        } catch {
            if (mounted.current && !signal?.aborted && startedAt === generation.current && attempt === verification.current) setChecking(false);
        }
    }, []);

    const authorized: AuthenticatedRequest = useCallback(async (operation, currentSession) => {
        function verify() {
            if (sessionRef.current !== currentSession) throw new DOMException('Session changed.', 'AbortError');
        }

        try {
            verify();
            let flight = csrfFlight.current;
            if (flight?.sessionId !== currentSession) {
                flight = {sessionId: currentSession, promise: api.csrf().then(value => value.requestToken)};
                csrfFlight.current = flight;
                flight.promise.catch(() => {
                    if (csrfFlight.current === flight) csrfFlight.current = null;
                });
            }
            const csrf = await flight.promise;
            verify();
            const result = await operation(csrf);
            verify();
            return result;
        } catch (error) {
            if (error instanceof ApiError && error.code === 'antiforgery_failed' && csrfFlight.current?.sessionId === currentSession) csrfFlight.current = null;
            unauthorized(error, currentSession);
            throw error;
        }
    }, [unauthorized]);

    useEffect(() => {
        mounted.current = true;
        sessionStorage.removeItem(sessionKey);
        const controller = new AbortController();
        void checkSession(controller.signal);
        const refresh = () => {
            if (!document.hidden) void checkSession(controller.signal);
        };
        window.addEventListener('focus', refresh);
        document.addEventListener('visibilitychange', refresh);
        return () => {
            mounted.current = false;
            controller.abort();
            window.removeEventListener('focus', refresh);
            document.removeEventListener('visibilitychange', refresh);
        };
    }, [checkSession]);

    function login() {
        setLoginError(null);
        api.login();
    }

    async function logout() {
        const currentSession = sessionRef.current;
        if (!currentSession) return;
        await authorized(csrf => api.logout(csrf), currentSession);
        clear(currentSession);
    }

    return {sessionId, checking, expired, loginError, login, logout, unauthorized, authorized};
}
