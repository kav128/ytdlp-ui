import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, type AccessToken } from '../../api/client';
import { sessionKey, useAuth } from './useAuth';

const session: AccessToken = { accessToken: 'first-access', refreshToken: 'first-refresh', expiresAt: '2099-01-01T00:00:00Z', tokenType: 'Bearer' };
const next: AccessToken = { ...session, accessToken: 'next-access', refreshToken: 'next-refresh' };

beforeEach(() => {
  sessionStorage.setItem(sessionKey, JSON.stringify(session));
  vi.spyOn(api, 'session').mockResolvedValue({ authenticated: true });
  vi.spyOn(api, 'refresh').mockResolvedValue(next);
  vi.spyOn(api, 'logout').mockResolvedValue(undefined);
  vi.spyOn(api, 'login').mockResolvedValue(next);
});
afterEach(() => { vi.restoreAllMocks(); sessionStorage.clear(); });

async function signedIn() {
  const hook = renderHook(useAuth);
  await waitFor(() => expect(hook.result.current.sessionId).toBe(session.accessToken));
  return hook;
}

describe('refresh tokens', () => {
  it('restores an expired stored access token through refresh before checking the session', async () => {
    sessionStorage.setItem(sessionKey, JSON.stringify({ ...session, expiresAt: '2000-01-01T00:00:00Z' }));
    const { result } = await signedIn();
    expect(api.refresh).toHaveBeenCalledExactlyOnceWith(session.refreshToken);
    expect(api.session).toHaveBeenCalledWith(next.accessToken, expect.any(AbortSignal));
    expect(JSON.parse(sessionStorage.getItem(sessionKey)!)).toEqual(next);
    expect(result.current.sessionId).toBe(session.accessToken);
  });

  it('retries a protected 401 once with the rotated access token', async () => {
    const { result } = await signedIn();
    const operation = vi.fn<(token: string) => Promise<string>>().mockRejectedValueOnce(new ApiError(401, 'unauthorized')).mockResolvedValue('done');
    await act(async () => expect(await result.current.authorized(operation, session.accessToken)).toBe('done'));
    expect(operation.mock.calls).toEqual([[session.accessToken], [next.accessToken]]);
    expect(api.refresh).toHaveBeenCalledTimes(1);
    expect(result.current.sessionId).toBe(session.accessToken);
  });

  it('shares a single refresh between concurrent protected requests', async () => {
    const { result } = await signedIn();
    let complete!: (tokens: AccessToken) => void;
    vi.mocked(api.refresh).mockReturnValue(new Promise(resolve => { complete = resolve; }));
    const first = vi.fn<(token: string) => Promise<string>>().mockRejectedValueOnce(new ApiError(401, 'unauthorized')).mockResolvedValue('one');
    const second = vi.fn<(token: string) => Promise<string>>().mockRejectedValueOnce(new ApiError(401, 'unauthorized')).mockResolvedValue('two');
    const one = result.current.authorized(first, session.accessToken);
    const two = result.current.authorized(second, session.accessToken);
    await waitFor(() => expect(api.refresh).toHaveBeenCalledTimes(1));
    await act(async () => { complete(next); expect(await Promise.all([one, two])).toEqual(['one', 'two']); });
    expect(first).toHaveBeenLastCalledWith(next.accessToken);
    expect(second).toHaveBeenLastCalledWith(next.accessToken);
  });

  it('uses an already refreshed pair for a late 401 from the old access token', async () => {
    const { result } = await signedIn();
    let fail!: (error: unknown) => void;
    const late = vi.fn<(token: string) => Promise<string>>().mockReturnValueOnce(new Promise((_, reject) => { fail = reject; })).mockResolvedValue('done');
    const pending = result.current.authorized(late, session.accessToken);
    const other = vi.fn<(token: string) => Promise<string>>().mockRejectedValueOnce(new ApiError(401, 'unauthorized')).mockResolvedValue('other');
    await act(async () => { await result.current.authorized(other, session.accessToken); fail(new ApiError(401, 'unauthorized')); await pending; });
    expect(api.refresh).toHaveBeenCalledTimes(1);
    expect(late).toHaveBeenLastCalledWith(next.accessToken);
  });

  it('clears tokens only after a final 401 and does not retry a second protected 401', async () => {
    const { result } = await signedIn();
    const operation = vi.fn<(token: string) => Promise<void>>().mockRejectedValue(new ApiError(401, 'unauthorized'));
    await act(async () => { await expect(result.current.authorized(operation, session.accessToken)).rejects.toMatchObject({ status: 401 }); });
    expect(operation).toHaveBeenCalledTimes(2);
    expect(api.refresh).toHaveBeenCalledTimes(1);
    expect(sessionStorage.getItem(sessionKey)).toBeNull();
    expect(result.current.sessionId).toBeNull();
    expect(result.current.expired).toBe(true);
  });

  it('keeps the session when refresh fails with a server error', async () => {
    const { result } = await signedIn();
    vi.mocked(api.refresh).mockRejectedValue(new ApiError(500, 'request_failed'));
    await act(async () => { await expect(result.current.authorized(() => Promise.reject(new ApiError(401, 'unauthorized')), session.accessToken)).rejects.toMatchObject({ status: 500 }); });
    expect(JSON.parse(sessionStorage.getItem(sessionKey)!)).toEqual(session);
    expect(result.current.sessionId).toBe(session.accessToken);
    expect(result.current.expired).toBe(false);
  });

  it('does not save a late refresh after logout', async () => {
    const { result } = await signedIn();
    let complete!: (tokens: AccessToken) => void;
    vi.mocked(api.refresh).mockReturnValue(new Promise(resolve => { complete = resolve; }));
    const pending = result.current.authorized(() => Promise.reject(new ApiError(401, 'unauthorized')), session.accessToken).catch(error => error);
    await waitFor(() => expect(api.refresh).toHaveBeenCalledTimes(1));
    await act(async () => { await result.current.logout(); complete(next); expect(await pending).toMatchObject({ name: 'AbortError' }); });
    expect(sessionStorage.getItem(sessionKey)).toBeNull();
    expect(result.current.sessionId).toBeNull();
  });

  it('does not overwrite a new login with a late refresh from the previous session', async () => {
    const { result } = await signedIn();
    let complete!: (tokens: AccessToken) => void;
    vi.mocked(api.refresh).mockReturnValue(new Promise(resolve => { complete = resolve; }));
    const pending = result.current.authorized(() => Promise.reject(new ApiError(401, 'unauthorized')), session.accessToken).catch(error => error);
    await waitFor(() => expect(api.refresh).toHaveBeenCalledTimes(1));
    await act(async () => { await result.current.login('admin', 'password'); complete({ ...next, accessToken: 'old-refresh-result' }); await pending; });
    await waitFor(() => expect(result.current.sessionId).toBe(next.accessToken));
    expect(JSON.parse(sessionStorage.getItem(sessionKey)!)).toEqual(next);
  });

  it('does not abort a shared refresh when one caller is canceled', async () => {
    const { result } = await signedIn();
    let complete!: (tokens: AccessToken) => void;
    vi.mocked(api.refresh).mockReturnValue(new Promise(resolve => { complete = resolve; }));
    const controller = new AbortController();
    const canceled = vi.fn<(token: string) => Promise<string>>().mockRejectedValueOnce(new ApiError(401, 'unauthorized'))
      .mockImplementation(() => controller.signal.aborted ? Promise.reject(new DOMException('Aborted', 'AbortError')) : Promise.resolve('one'));
    const other = vi.fn<(token: string) => Promise<string>>().mockRejectedValueOnce(new ApiError(401, 'unauthorized')).mockResolvedValue('two');
    const one = result.current.authorized(canceled, session.accessToken).catch(error => error);
    const two = result.current.authorized(other, session.accessToken);
    await waitFor(() => expect(api.refresh).toHaveBeenCalledTimes(1));
    await act(async () => { controller.abort(); complete(next); expect(await one).toMatchObject({ name: 'AbortError' }); expect(await two).toBe('two'); });
    expect(result.current.sessionId).toBe(session.accessToken);
  });
});
