import {act, fireEvent, render, screen, waitFor, within} from '@testing-library/react';
import {beforeEach, afterEach, describe, expect, it, vi} from 'vitest';
import App from './App';
import {api, ApiError, type Download, type Session} from './api/client';
import {messages} from './locales';
import {makeAttempt, makeDownload, makeStage} from './test/downloads';

const session: Session = {authenticated: true, sessionId: 'server-session-one'};
const download = makeDownload();

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
    it('lets an administrator save and remove a title while keeping the original download link', async () => {
        const file = (await api.library())[0];
        const update = vi.spyOn(api, 'setFileTitle').mockImplementation(async (_id, title) => {
            vi.mocked(api.library).mockResolvedValue([{...file, title}]);
        });
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: `${messages.editFileTitle}: ${file.name}`}));
        const editor = within(screen.getByRole('dialog'));
        expect(editor.getByText(file.name)).toBeInTheDocument();
        fireEvent.change(editor.getByLabelText(messages.fileTitle), {target: {value: 'Public title'}});
        fireEvent.click(editor.getByRole('button', {name: messages.saveFileTitle}));

        expect(await screen.findByRole('heading', {name: 'Public title'})).toBeInTheDocument();
        expect(update).toHaveBeenCalledWith(file.id, 'Public title', 'csrf-token');
        expect(screen.getByRole('link', {name: `${messages.download}: Public title`})).toHaveAttribute('href', file.contentUrl);

        fireEvent.click(screen.getByRole('button', {name: `${messages.editFileTitle}: Public title`}));
        const editAgain = within(screen.getByRole('dialog'));
        expect(editAgain.getByLabelText(messages.fileTitle)).toHaveValue('Public title');
        fireEvent.click(editAgain.getByRole('button', {name: messages.removeFileTitle}));
        expect(await screen.findByRole('heading', {name: file.name})).toBeInTheDocument();
        expect(update).toHaveBeenLastCalledWith(file.id, null, 'csrf-token');
    });

    it('shows titles to anonymous visitors without the edit action', async () => {
        const file = (await api.library())[0];
        vi.mocked(api.session).mockResolvedValue({authenticated: false, sessionId: null});
        vi.mocked(api.library).mockResolvedValue([{...file, title: 'Public title'}]);
        render(<App/>);
        expect(await screen.findByRole('heading', {name: 'Public title'})).toBeInTheDocument();
        expect(screen.queryByRole('button', {name: `${messages.editFileTitle}: Public title`})).not.toBeInTheDocument();
    });

    it('updates an open history from the current poll instead of retaining a snapshot', async () => {
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.history}));
        expect(within(screen.getByRole('dialog')).getAllByText(messages.executionStatuses.Failed).length).toBeGreaterThan(0);
        vi.mocked(api.downloads).mockResolvedValue([makeDownload({
            state: 'Completed', resumeState: null, failedState: null,
            error: null, errorCode: null, allowedActions: [], attempts: [makeAttempt({
                state: 'Completed', status: 'Succeeded',
                error: null, errorCode: null, stages: [makeStage({status: 'Succeeded', errorCode: null, progress: 100})]
            })]
        })]);
        fireEvent.focus(window);
        await waitFor(() => expect(within(screen.getByRole('dialog')).getAllByText(messages.states.Completed).length).toBeGreaterThan(0));
        expect(within(screen.getByRole('dialog')).queryByText('Demo error')).not.toBeInTheDocument();
    });
    it('reloads the library when a private poll confirms a new publication', async () => {
        const pending = deferred<Download[]>();
        vi.mocked(api.downloads).mockReturnValue(pending.promise);
        vi.mocked(api.library).mockResolvedValueOnce([]).mockResolvedValue([{
            id: 'published', name: 'Published video.mp4', size: 100,
            lastModified: '2026-10-01T10:00:00Z', contentUrl: '/api/library/published/content'
        }]);
        render(<App/>);
        expect(await screen.findByText(messages.emptyLibrary)).toBeInTheDocument();
        await act(async () => pending.resolve([makeDownload({
            state: 'Completed',
            error: null,
            errorCode: null,
            allowedActions: []
        })]));
        expect(await screen.findByText('Published video.mp4')).toBeInTheDocument();
        expect(api.library).toHaveBeenCalledTimes(2);
    });
    it('updates a pending cancellation confirmation when the server removes that action', async () => {
        vi.mocked(api.downloads).mockResolvedValue([makeDownload({
            state: 'Uploading',
            allowedActions: ['cancel'],
            error: null,
            errorCode: null
        })]);
        vi.spyOn(api, 'act').mockResolvedValue(download);
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.cancelDownload}));
        vi.mocked(api.downloads).mockResolvedValue([makeDownload({
            state: 'CleaningUp',
            allowedActions: [],
            error: null,
            errorCode: null
        })]);
        fireEvent.focus(window);
        expect(await within(screen.getByRole('dialog')).findByText(messages.errors.invalid_state)).toBeInTheDocument();
        expect(within(screen.getByRole('dialog')).getByRole('button', {name: messages.cancelDownload})).toBeDisabled();
        expect(api.act).not.toHaveBeenCalled();
    });
    it('sends one durable create and disables all dialog close actions while it is pending', async () => {
        const pending = deferred<Download>();
        vi.spyOn(api, 'create').mockReturnValue(pending.promise);
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: messages.newDownload}));
        const input = screen.getByRole('textbox', {name: messages.url});
        fireEvent.change(input, {target: {value: 'https://example.test/once'}});
        fireEvent.submit(input.closest('form')!);
        fireEvent.submit(input.closest('form')!);
        await waitFor(() => expect(api.create).toHaveBeenCalledTimes(1));
        expect(screen.getByRole('dialog')).toHaveAttribute('aria-busy', 'true');
        expect(within(screen.getByRole('dialog')).getByRole('button', {name: messages.close})).toBeDisabled();
        await act(async () => pending.resolve(download));
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    });
    it.each([true, false])('distinguishes queued own deletion from confirmed external deletion (queued=%s)', async queued => {
        vi.spyOn(api, 'deleteFile').mockResolvedValue(queued ? {downloadId: 'own-job'} : undefined);
        render(<App/>);
        fireEvent.click(await screen.findByRole('button', {name: `${messages.delete}: Public notes.txt`}));
        if (!queued) vi.mocked(api.library).mockResolvedValue([]);
        fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', {name: messages.delete}));
        expect(await screen.findByText(queued ? messages.fileDeleteQueued : messages.fileRemoved)).toBeInTheDocument();
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        if (queued) {
            expect(screen.getByText('Public notes.txt')).toBeInTheDocument();
            expect(screen.queryByText(messages.fileRemoved)).not.toBeInTheDocument();
            vi.mocked(api.library).mockResolvedValue([]);
            await waitFor(() => expect(screen.getByRole('button', {name: messages.refresh})).not.toBeDisabled());
            fireEvent.click(screen.getByRole('button', {name: messages.refresh}));
        }
        await waitFor(() => expect(screen.queryByText('Public notes.txt')).not.toBeInTheDocument());
    });
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
        expect(await screen.findByText('Private video')).toBeInTheDocument();
        fireEvent.click(await screen.findByRole('button', {name: messages.uploadFile}));
        fireEvent.change(screen.getByLabelText(messages.chooseFile), {target: {files: [new File(['x'], 'old.bin')]}});
        fireEvent.submit(screen.getByLabelText(messages.chooseFile).closest('form')!);
        await waitFor(() => expect(api.upload).toHaveBeenCalledTimes(1));
        vi.mocked(api.downloads).mockRejectedValueOnce(new ApiError(401, 'unauthorized'));
        fireEvent(document, new Event('visibilitychange'));
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        await waitFor(() => expect(screen.queryByText('Private video')).not.toBeInTheDocument());
        expect(await within(screen.getByRole('banner')).findByRole('button', {name: messages.login})).toBeInTheDocument();
        vi.mocked(api.session).mockResolvedValue({authenticated: true, sessionId: 'new-cookie-session'});
        fireEvent.focus(window);
        expect(await screen.findByText('Private video')).toBeInTheDocument();
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
