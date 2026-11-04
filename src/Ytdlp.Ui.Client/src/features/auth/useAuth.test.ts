import {act, fireEvent, renderHook, waitFor} from '@testing-library/react';
import {beforeEach, afterEach, describe, expect, it, vi} from 'vitest';
import {api, ApiError, type Session} from '../../api/client';
import {useAuth, sessionKey} from './useAuth';

const session: Session = {authenticated: true, sessionId: 'server-session-one'};

function deferred<T>() {
    let resolve!: (value: T) => void;
    let reject!: (error: unknown) => void;
    const promise = new Promise<T>((yes, no) => {
        resolve = yes;
        reject = no;
    });
    return {promise, resolve, reject};
}

async function signedIn() {
    const hook = renderHook(useAuth);
    await waitFor(() => expect(hook.result.current.sessionId).toBe(session.sessionId));
    return hook;
}

beforeEach(() => {
    sessionStorage.clear();
    vi.spyOn(api, 'session').mockResolvedValue(session);
    vi.spyOn(api, 'csrf').mockResolvedValue({requestToken: 'csrf-request-token'});
    vi.spyOn(api, 'logout').mockResolvedValue(undefined);
    vi.spyOn(api, 'login').mockImplementation(() => {
    });
});
afterEach(() => vi.restoreAllMocks());

describe('server session', () => {
    it('checks the server before granting access and removes legacy browser tokens', async () => {
        sessionStorage.setItem(sessionKey, JSON.stringify({accessToken: 'old-access', refreshToken: 'old-refresh'}));
        const response = deferred<Session>();
        vi.mocked(api.session).mockReturnValue(response.promise);
        const {result} = renderHook(useAuth);
        expect(result.current.sessionId).toBeNull();
        expect(sessionStorage.getItem(sessionKey)).toBeNull();
        await act(async () => response.resolve(session));
        expect(result.current.sessionId).toBe(session.sessionId);
        expect(api).not.toHaveProperty('refresh');
        expect(sessionStorage.length).toBe(0);
    });
    it('uses SSO navigation without accepting credentials', async () => {
        const {result} = await signedIn();
        act(() => result.current.login());
        expect(api.login).toHaveBeenCalledWith();
    });
    it('shares one in-memory CSRF fetch between parallel requests', async () => {
        const {result} = await signedIn();
        const response = deferred<{ requestToken: string }>();
        vi.mocked(api.csrf).mockReturnValue(response.promise);
        const one = vi.fn(async (csrf: string) => csrf + '-one');
        const two = vi.fn(async (csrf: string) => csrf + '-two');
        let first!: Promise<string>;
        let second!: Promise<string>;
        act(() => {
            first = result.current.authorized(one, session.sessionId!);
            second = result.current.authorized(two, session.sessionId!);
        });
        expect(api.csrf).toHaveBeenCalledTimes(1);
        await act(async () => {
            response.resolve({requestToken: 'shared-csrf'});
            expect(await first).toBe('shared-csrf-one');
            expect(await second).toBe('shared-csrf-two');
        });
    });
    it('clears private state on 401 without replaying a command', async () => {
        const {result} = await signedIn();
        const operation = vi.fn().mockRejectedValue(new ApiError(401, 'unauthorized'));
        await act(async () => {
            await expect(result.current.authorized(operation, session.sessionId!)).rejects.toBeInstanceOf(ApiError);
        });
        expect(operation).toHaveBeenCalledTimes(1);
        expect(result.current.sessionId).toBeNull();
        expect(result.current.expired).toBe(true);
    });
    it('keeps the session after transient network or 500 errors', async () => {
        const {result} = await signedIn();
        for (const error of [new TypeError('offline'), new ApiError(500, 'request_failed')]) {
            await act(async () => {
                await expect(result.current.authorized(() => Promise.reject(error), session.sessionId!)).rejects.toBe(error);
            });
            expect(result.current.sessionId).toBe(session.sessionId);
        }
    });
    it('does not restore logout from an older pending session check', async () => {
        const {result} = await signedIn();
        const check = deferred<Session>();
        vi.mocked(api.session).mockReturnValue(check.promise);
        fireEvent.focus(window);
        await act(async () => result.current.logout());
        await act(async () => check.resolve(session));
        expect(result.current.sessionId).toBeNull();
    });
    it('ignores success and 401 from a previous server session', async () => {
        const {result} = await signedIn();
        const response = deferred<string>();
        const operation = vi.fn(() => response.promise);
        const pending = result.current.authorized(operation, session.sessionId!).catch(error => error);
        await waitFor(() => expect(operation).toHaveBeenCalled());
        vi.mocked(api.session).mockResolvedValue({authenticated: true, sessionId: 'new-server-session'});
        fireEvent.focus(window);
        await waitFor(() => expect(result.current.sessionId).toBe('new-server-session'));
        await act(async () => response.resolve('old-result'));
        expect(await pending).toBeInstanceOf(DOMException);
        act(() => result.current.unauthorized(new ApiError(401, 'unauthorized'), session.sessionId!));
        expect(result.current.sessionId).toBe('new-server-session');
    });
    it('detects logout from another tab when returning to the page', async () => {
        const {result} = await signedIn();
        vi.mocked(api.session).mockResolvedValue({authenticated: false, sessionId: null});
        fireEvent.focus(window);
        await waitFor(() => expect(result.current.sessionId).toBeNull());
        expect(result.current.expired).toBe(true);
        vi.mocked(api.session).mockResolvedValue({authenticated: true, sessionId: 'restored-server-session'});
        fireEvent.focus(window);
        await waitFor(() => expect(result.current.sessionId).toBe('restored-server-session'));
        expect(result.current.expired).toBe(false);
    });
    it('preserves the current session when a visibility check fails', async () => {
        const {result} = await signedIn();
        vi.mocked(api.session).mockRejectedValue(new TypeError('offline'));
        fireEvent(document, new Event('visibilitychange'));
        await waitFor(() => expect(api.session).toHaveBeenCalledTimes(2));
        expect(result.current.sessionId).toBe(session.sessionId);
    });
});
