import {act, fireEvent, render, screen, waitFor} from '@testing-library/react';
import {beforeEach, afterEach, describe, expect, it, vi} from 'vitest';
import App from './App';
import {api, ApiError, type Download, type Session} from './api/client';
import {messages} from './locales';

const session: Session = {authenticated: true, sessionId: 'server-session-one'};
const download: Download = {
    id: 'private-job',
    title: 'Private video',
    url: 'https://example.com/video',
    state: 'Failed',
    resumeState: 'CleaningUp',
    progress: null,
    createdAt: '2026-10-01T10:00:00Z',
    error: 'Demo error',
    errorCode: 'cleanup_failed',
    allowedActions: ['retry'],
    attempts: [{number: 1, state: 'Failed', createdAt: '2026-10-01T10:00:00Z', error: 'Demo error'}],
};

function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>(yes => {
        resolve = yes;
    });
    return {promise, resolve};
}

beforeEach(() => {
    sessionStorage.clear();
    window.history.replaceState({}, '', '/');
    vi.spyOn(api, 'library').mockResolvedValue([{
        id: 'public-file',
        name: 'Public notes.txt',
        size: 512,
        lastModified: '2026-10-01T10:00:00Z',
        contentUrl: '/api/library/public-file/content'
    }]);
    vi.spyOn(api, 'downloads').mockResolvedValue([download]);
    vi.spyOn(api, 'session').mockResolvedValue(session);
    vi.spyOn(api, 'csrf').mockResolvedValue({requestToken: 'csrf-token'});
    vi.spyOn(api, 'logout').mockResolvedValue(undefined);
    vi.spyOn(api, 'login').mockImplementation(() => {
    });
});
afterEach(() => {
    vi.restoreAllMocks();
    window.history.replaceState({}, '', '/');
});

describe('library and SSO workspace', () => {
    it('preserves the original URL input while validating its trimmed form', async () => {
        const create = vi.spyOn(api, 'create').mockResolvedValue(download);
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.newDownload}));
        const original = '  HTTPS://EXAMPLE.test:443/Case?b=2&a=1  ';
        const input = screen.getByRole('textbox', {name: messages.url});
        fireEvent.change(input, {target: {value: original}});
        fireEvent.submit(input.closest('form')!);
        await waitFor(() => expect(create).toHaveBeenCalledWith(original, 'csrf-token'));
    });
    it('publishes a file once and reloads the public catalog after confirmation', async () => {
        const complete = deferred<{
            id: string;
            name: string;
            size: number;
            lastModified: string;
            contentUrl: string
        }>();
        const upload = vi.spyOn(api, 'upload').mockReturnValue(complete.promise);
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.uploadFile}));
        fireEvent.change(screen.getByLabelText(messages.chooseFile), {target: {files: [new File(['x'], 'note.txt')]}});
        const form = screen.getByLabelText(messages.chooseFile).closest('form')!;
        fireEvent.submit(form);
        fireEvent.submit(form);
        await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));
        expect(screen.getByText(messages.uploadingFile)).toBeInTheDocument();
        const before = vi.mocked(api.library).mock.calls.length;
        await act(async () => complete.resolve({
            id: 'new',
            name: 'note.txt',
            size: 1,
            lastModified: '',
            contentUrl: ''
        }));
        expect(await screen.findByText(messages.fileUploaded)).toBeInTheDocument();
        await waitFor(() => expect(api.library).toHaveBeenCalledTimes(before + 1));
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    it('keeps an upload error visible without claiming publication', async () => {
        vi.spyOn(api, 'upload').mockRejectedValue(new ApiError(503, 'storage_unavailable'));
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.uploadFile}));
        fireEvent.change(await screen.findByLabelText(messages.chooseFile), {target: {files: [new File([], 'empty.bin')]}});
        fireEvent.submit(screen.getByLabelText(messages.chooseFile).closest('form')!);
        expect(await screen.findByRole('alert')).toHaveTextContent(messages.errors.storage_unavailable);
        expect(screen.queryByText(messages.fileUploaded)).not.toBeInTheDocument();
    });
    it('ignores an old upload completion after another cookie session opens a new upload form', async () => {
        const oldUpload = deferred<{
            id: string;
            name: string;
            size: number;
            lastModified: string;
            contentUrl: string
        }>();
        vi.spyOn(api, 'upload').mockReturnValue(oldUpload.promise);
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.uploadFile}));
        fireEvent.change(screen.getByLabelText(messages.chooseFile), {target: {files: [new File(['x'], 'old.bin')]}});
        fireEvent.submit(screen.getByLabelText(messages.chooseFile).closest('form')!);
        await waitFor(() => expect(api.upload).toHaveBeenCalledTimes(1));
        vi.mocked(api.downloads).mockRejectedValueOnce(new ApiError(401, 'unauthorized'));
        fireEvent(document, new Event('visibilitychange'));
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        vi.mocked(api.session).mockResolvedValue({authenticated: true, sessionId: 'new-cookie-session'});
        fireEvent.focus(window);
        fireEvent.click(await screen.findByRole('button', {name: messages.uploadFile}));
        fireEvent.change(screen.getByLabelText(messages.chooseFile), {target: {files: [new File(['new'], 'new.bin')]}});
        await act(async () => oldUpload.resolve({
            id: 'old',
            name: 'old.bin',
            size: 1,
            lastModified: '',
            contentUrl: ''
        }));
        expect(screen.getByRole('dialog')).toBeInTheDocument();
        expect((screen.getByLabelText(messages.chooseFile) as HTMLInputElement).files?.[0]?.name).toBe('new.bin');
        expect(screen.queryByText(messages.fileUploaded)).not.toBeInTheDocument();
    });
    it('shows public files without requesting private data anonymously', async () => {
        vi.mocked(api.session).mockResolvedValue({authenticated: false, sessionId: null});
        render(<App/>);
        expect(await screen.findByText('Public notes.txt')).toBeInTheDocument();
        expect(await screen.findByRole('button', {name: messages.login})).toBeInTheDocument();
        expect(screen.queryByRole('heading', {name: messages.downloads})).not.toBeInTheDocument();
        expect(api.downloads).not.toHaveBeenCalled();
    });
    it('waits for a server-confirmed session before reading closed data', async () => {
        const check = deferred<Session>();
        vi.mocked(api.session).mockReturnValue(check.promise);
        render(<App/>);
        expect(api.downloads).not.toHaveBeenCalled();
        await act(async () => check.resolve(session));
        expect(await screen.findByText('Private video')).toBeInTheDocument();
        expect(screen.queryByRole('button', {name: messages.cancelDownload})).not.toBeInTheDocument();
    });
    it('starts SSO without displaying a local password form', async () => {
        vi.mocked(api.session).mockResolvedValue({authenticated: false, sessionId: null});
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.login}));
        expect(api.login).toHaveBeenCalledWith();
        expect(document.querySelector('input[type="password"]')).toBeNull();
    });
    it('shows a localized group refusal while keeping the public library', async () => {
        window.history.replaceState({}, '', '/?authError=access_denied');
        vi.mocked(api.session).mockResolvedValue({authenticated: false, sessionId: null});
        render(<App/>);
        expect(await screen.findByText('Public notes.txt')).toBeInTheDocument();
        expect(screen.getByRole('alert')).toHaveTextContent(messages.errors.access_denied);
        expect(api.downloads).not.toHaveBeenCalled();
    });
    it('clears administrative data after local logout', async () => {
        render(<App/>);
        expect(await screen.findByText('Private video')).toBeInTheDocument();
        fireEvent.click(screen.getByRole('button', {name: messages.logout}));
        await waitFor(() => expect(screen.queryByText('Private video')).not.toBeInTheDocument());
        expect(api.logout).toHaveBeenCalledWith('csrf-token');
        expect(sessionStorage.length).toBe(0);
    });
    it('validates URLs and sends commands with CSRF rather than Bearer', async () => {
        vi.spyOn(api, 'create').mockResolvedValue(download);
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.newDownload}));
        const input = screen.getByRole('textbox', {name: messages.url});
        fireEvent.change(input, {target: {value: 'ftp://example.com/video'}});
        fireEvent.submit(input.closest('form')!);
        expect(await screen.findByText(messages.errors.invalid_url)).toBeInTheDocument();
        expect(api.create).not.toHaveBeenCalled();
        fireEvent.change(input, {target: {value: 'https://example.com/new'}});
        fireEvent.submit(input.closest('form')!);
        await waitFor(() => expect(api.create).toHaveBeenCalledWith('https://example.com/new', 'csrf-token'));
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    });
    it('does not close or reset a new URL form after an old successful create response', async () => {
        const creation = deferred<Download>();
        vi.spyOn(api, 'create').mockReturnValue(creation.promise);
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.newDownload}));
        fireEvent.change(screen.getByRole('textbox', {name: messages.url}), {target: {value: 'https://example.com/old'}});
        fireEvent.submit(screen.getByRole('textbox', {name: messages.url}).closest('form')!);
        await waitFor(() => expect(api.create).toHaveBeenCalled());
        vi.mocked(api.downloads).mockRejectedValueOnce(new ApiError(401, 'unauthorized'));
        fireEvent(document, new Event('visibilitychange'));
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        vi.mocked(api.session).mockResolvedValue({authenticated: true, sessionId: 'server-session-two'});
        fireEvent.focus(window);
        fireEvent.click(await screen.findByRole('button', {name: messages.newDownload}));
        fireEvent.change(screen.getByRole('textbox', {name: messages.url}), {target: {value: 'https://example.com/new-form'}});
        await act(async () => creation.resolve(download));
        expect(screen.getByRole('dialog')).toBeInTheDocument();
        expect(screen.getByRole('textbox', {name: messages.url})).toHaveValue('https://example.com/new-form');
    });
    it('clears the workspace on a protected 401', async () => {
        vi.spyOn(api, 'act').mockRejectedValue(new ApiError(401, 'unauthorized'));
        render(<App/>);
        expect(await screen.findByText('Private video')).toBeInTheDocument();
        fireEvent.click(screen.getByRole('button', {name: messages.retry}));
        await waitFor(() => expect(screen.queryByText('Private video')).not.toBeInTheDocument());
        expect(api.act).toHaveBeenCalledTimes(1);
    });
});
