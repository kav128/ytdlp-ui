import {act, fireEvent, renderHook} from '@testing-library/react';
import {afterEach, describe, expect, it, vi} from 'vitest';
import {api, ApiError, type Download} from '../../api/client';
import {makeDownload} from '../../test/downloads';
import type {AuthenticatedRequest} from '../auth/useAuth';
import {useDownloads} from './useDownloads';

const authorized: AuthenticatedRequest = async operation => operation('csrf');

function deferred<T>() {
    let resolve!: (value: T) => void;
    return {
        promise: new Promise<T>(yes => {
            resolve = yes;
        }), resolve: (value: T) => resolve(value)
    };
}

afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
});

describe('private polling', () => {
    it('never requests private data for an anonymous visitor, including focus and timer refreshes', async () => {
        vi.useFakeTimers();
        const request = vi.spyOn(api, 'downloads');
        const hook = renderHook(() => useDownloads(null, 0, authorized, vi.fn()));
        fireEvent.focus(window);
        await act(async () => {
            await vi.advanceTimersByTimeAsync(10000);
        });
        expect(request).not.toHaveBeenCalled();
        expect(hook.result.current.downloads).toEqual([]);
        hook.unmount();
    });
    it('ignores a late session A result after B is confirmed and clears B data on logout', async () => {
        const pending = deferred<Download[]>();
        const request = vi.spyOn(api, 'downloads').mockReturnValueOnce(pending.promise).mockResolvedValue([makeDownload({title: 'Session B video'})]);
        const unauthorized = vi.fn();
        const hook = renderHook(({session}) => useDownloads(session, 0, authorized, unauthorized), {initialProps: {session: 'A' as string | null}});
        await act(async () => {
        });
        const signal = request.mock.calls[0][0];
        hook.rerender({session: 'B'});
        await act(async () => {
        });
        expect(signal?.aborted).toBe(true);
        expect(hook.result.current.downloads[0].title).toBe('Session B video');
        await act(async () => pending.resolve([makeDownload({title: 'Session A secret'})]));
        expect(hook.result.current.downloads[0].title).toBe('Session B video');
        hook.rerender({session: null});
        expect(hook.result.current.downloads).toEqual([]);
        fireEvent.focus(window);
        expect(request).toHaveBeenCalledTimes(2);
        hook.unmount();
    });
    it('does not overlap polls, pauses while hidden, and recovers a failed refresh without inventing progress', async () => {
        vi.useFakeTimers();
        const pending = deferred<Download[]>();
        const request = vi.spyOn(api, 'downloads').mockReturnValueOnce(pending.promise).mockRejectedValueOnce(new ApiError(503, 'request_failed')).mockResolvedValue([makeDownload({progress: 37})]);
        const unauthorized = vi.fn();
        const hook = renderHook(() => useDownloads('A', 0, authorized, unauthorized));
        await act(async () => {
        });
        fireEvent.focus(window);
        await act(async () => {
            await vi.advanceTimersByTimeAsync(5000);
        });
        expect(request).toHaveBeenCalledTimes(1);
        await act(async () => pending.resolve([makeDownload({progress: null})]));
        const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
        await act(async () => {
            await vi.advanceTimersByTimeAsync(5000);
        });
        expect(request).toHaveBeenCalledTimes(1);
        hidden.mockReturnValue(false);
        fireEvent(document, new Event('visibilitychange'));
        await act(async () => {
        });
        expect(hook.result.current.error).toBe(true);
        expect(hook.result.current.downloads[0].progress).toBeNull();
        fireEvent.focus(window);
        await act(async () => {
        });
        expect(hook.result.current.error).toBe(false);
        expect(hook.result.current.downloads[0].progress).toBe(37);
        hook.unmount();
    });
});
