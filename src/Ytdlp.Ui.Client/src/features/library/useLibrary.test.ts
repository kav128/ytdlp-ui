import {act, fireEvent, renderHook} from '@testing-library/react';
import {afterEach, describe, expect, it, vi} from 'vitest';
import {api, type LibraryFile} from '../../api/client';
import {useLibrary} from './useLibrary';

const file: LibraryFile = {
    id: 'public',
    name: 'public.txt',
    size: 100,
    lastModified: '2026-10-01T10:00:00Z',
    contentUrl: '/api/library/public/content'
};
afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
});

describe('public catalog polling', () => {
    it('discovers worker publication and confirmed deletion without closed requests or manual refresh', async () => {
        vi.useFakeTimers();
        const privateRequest = vi.spyOn(api, 'downloads');
        vi.spyOn(api, 'library').mockResolvedValueOnce([]).mockResolvedValueOnce([file]).mockResolvedValue([]);
        const hook = renderHook(() => useLibrary('initial'));
        await act(async () => {
        });
        expect(hook.result.current.files).toEqual([]);
        await act(async () => {
            await vi.advanceTimersByTimeAsync(5000);
        });
        expect(hook.result.current.files).toEqual([file]);
        await act(async () => {
            await vi.advanceTimersByTimeAsync(5000);
        });
        expect(hook.result.current.files).toEqual([]);
        expect(privateRequest).not.toHaveBeenCalled();
        hook.unmount();
    });
    it('ignores an older listing after a command starts a fresh revision', async () => {
        let finish!: (files: LibraryFile[]) => void;
        const first = new Promise<LibraryFile[]>(resolve => {
            finish = resolve;
        });
        const request = vi.spyOn(api, 'library').mockReturnValueOnce(first).mockResolvedValue([]);
        const hook = renderHook(({revision}) => useLibrary(revision), {initialProps: {revision: 'old'}});
        await act(async () => {
        });
        const signal = request.mock.calls[0][0];
        hook.rerender({revision: 'after-delete'});
        await act(async () => {
        });
        await act(async () => finish([file]));
        expect(signal?.aborted).toBe(true);
        expect(hook.result.current.files).toEqual([]);
        hook.unmount();
    });
    it('keeps the last confirmed public list on network failure and retries on focus without overlapping', async () => {
        let finish!: (files: LibraryFile[]) => void;
        const next = new Promise<LibraryFile[]>(resolve => {
            finish = resolve;
        });
        const request = vi.spyOn(api, 'library').mockResolvedValueOnce([file]).mockRejectedValueOnce(new Error('network')).mockReturnValue(next);
        const hook = renderHook(() => useLibrary('initial'));
        await act(async () => {
        });
        fireEvent.focus(window);
        await act(async () => {
        });
        expect(hook.result.current.error).toBe(true);
        expect(hook.result.current.files).toEqual([file]);
        fireEvent.focus(window);
        fireEvent.focus(window);
        await act(async () => {
        });
        expect(request).toHaveBeenCalledTimes(3);
        await act(async () => finish([]));
        expect(hook.result.current.error).toBe(false);
        hook.unmount();
    });
});
