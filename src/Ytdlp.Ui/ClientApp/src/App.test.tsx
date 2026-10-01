import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { api, ApiError, type Download } from './api/client';
import { sessionKey } from './features/auth/useAuth';
import { messages } from './locales';

const download: Download = {
  id: 'private-job', title: 'Private video', url: 'https://example.com/video', state: 'Failed', resumeState: 'CleaningUp', progress: null,
  createdAt: '2026-10-01T10:00:00Z', error: 'Demo error', errorCode: 'cleanup_failed', allowedActions: ['retry'],
  attempts: [{ number: 1, state: 'Failed', createdAt: '2026-10-01T10:00:00Z', error: 'Demo error' }],
};
const session = { accessToken: 'demo-token', expiresAt: '2026-10-02T10:00:00Z', tokenType: 'Bearer' as const };

beforeEach(() => {
  sessionStorage.clear();
  vi.spyOn(api, 'library').mockResolvedValue([{ id: 'public-file', name: 'Public notes.txt', size: 512, lastModified: '2026-10-01T10:00:00Z', contentUrl: '/api/library/public-file/content' }]);
  vi.spyOn(api, 'downloads').mockResolvedValue([download]);
  vi.spyOn(api, 'session').mockResolvedValue({ authenticated: true });
  vi.spyOn(api, 'logout').mockResolvedValue(undefined);
  vi.spyOn(api, 'login').mockResolvedValue(session);
});
afterEach(() => { vi.restoreAllMocks(); });

describe('library and administrator workspace', () => {
  it('shows public files and never requests download data anonymously', async () => {
    render(<App />);
    expect(await screen.findByText('Public notes.txt')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: messages.downloads })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Delete/ })).not.toBeInTheDocument();
    expect(api.downloads).not.toHaveBeenCalled();
  });

  it('checks a stored session before loading private data', async () => {
    sessionStorage.setItem(sessionKey, JSON.stringify(session));
    let verify!: (value: { authenticated: boolean }) => void;
    vi.mocked(api.session).mockReturnValue(new Promise(resolve => { verify = resolve; }));
    render(<App />);
    expect(api.downloads).not.toHaveBeenCalled();
    expect(screen.queryByText('Private video')).not.toBeInTheDocument();
    await act(async () => verify({ authenticated: true }));
    expect(await screen.findByText('Private video')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: messages.cancelDownload })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: messages.retry })).toBeInTheDocument();
  });

  it('signs in through the dialog, stores only a token and clears private data on logout', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: messages.login }));
    const dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(messages.username), { target: { value: 'admin' } });
    fireEvent.change(within(dialog).getByLabelText(messages.password), { target: { value: 'demo-password' } });
    fireEvent.click(within(dialog).getByRole('button', { name: messages.login }));
    expect(await screen.findByText('Private video')).toBeInTheDocument();
    expect(sessionStorage.getItem(sessionKey)).toContain(session.accessToken);
    expect(sessionStorage.getItem(sessionKey)).not.toContain('demo-password');
    fireEvent.click(screen.getByRole('button', { name: messages.logout }));
    await waitFor(() => expect(screen.queryByText('Private video')).not.toBeInTheDocument());
    expect(sessionStorage.getItem(sessionKey)).toBeNull();
    expect(screen.getByText('Public notes.txt')).toBeInTheDocument();
  });

  it('rejects invalid URL locally and submits a valid URL with the current token', async () => {
    sessionStorage.setItem(sessionKey, JSON.stringify(session));
    const create = vi.spyOn(api, 'create').mockResolvedValue(download);
    render(<App />);
    fireEvent.click(await screen.findByRole('button', { name: messages.newDownload }));
    const field = screen.getByLabelText(messages.url);
    fireEvent.change(field, { target: { value: 'ftp://example.com/file' } });
    fireEvent.click(screen.getByRole('button', { name: messages.submitDownload }));
    expect(screen.getByRole('alert')).toHaveTextContent(messages.errors.invalid_url);
    expect(create).not.toHaveBeenCalled();
    fireEvent.change(field, { target: { value: 'https://example.com/new-video' } });
    fireEvent.click(screen.getByRole('button', { name: messages.submitDownload }));
    await waitFor(() => expect(create).toHaveBeenCalledWith('https://example.com/new-video', session.accessToken));
    expect(await screen.findByText(messages.downloadCreated)).toBeInTheDocument();
  });

  it('clears the current session on a protected 401', async () => {
    sessionStorage.setItem(sessionKey, JSON.stringify(session));
    vi.mocked(api.downloads).mockRejectedValue(new ApiError(401, 'unauthorized'));
    render(<App />);
    expect(await screen.findByText(messages.sessionExpired)).toBeInTheDocument();
    expect(sessionStorage.getItem(sessionKey)).toBeNull();
    expect(screen.queryByRole('heading', { name: messages.downloads })).not.toBeInTheDocument();
  });

  it('keeps a late response from restoring data after logout', async () => {
    sessionStorage.setItem(sessionKey, JSON.stringify(session));
    let complete!: (value: Download[]) => void;
    vi.mocked(api.downloads).mockReturnValue(new Promise(resolve => { complete = resolve; }));
    render(<App />);
    fireEvent.click(await screen.findByRole('button', { name: messages.logout }));
    await waitFor(() => expect(sessionStorage.getItem(sessionKey)).toBeNull());
    await act(async () => complete([download]));
    expect(screen.queryByText('Private video')).not.toBeInTheDocument();
  });
});
