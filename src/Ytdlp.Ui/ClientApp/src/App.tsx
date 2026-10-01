import { useEffect, useRef, useState } from 'react';
import { api, ApiError, type Download, type LibraryFile } from './api/client';
import Dialog from './components/Dialog';
import Icon from './components/Icon';
import LoginDialog from './features/auth/LoginDialog';
import { useAuth } from './features/auth/useAuth';
import DownloadCard from './features/downloads/DownloadCard';
import HistoryDialog from './features/downloads/HistoryDialog';
import NewDownloadDialog from './features/downloads/NewDownloadDialog';
import { useDownloads } from './features/downloads/useDownloads';
import LibraryList from './features/library/LibraryList';
import { messages } from './locales';
import { errorLabel, formatNumber, formatSize } from './locales/format';

export default function App() {
  const auth = useAuth();
  const currentToken = useRef(auth.token);
  currentToken.current = auth.token;
  const [files, setFiles] = useState<LibraryFile[]>([]);
  const [libraryLoading, setLibraryLoading] = useState(true);
  const [libraryError, setLibraryError] = useState(false);
  const [revision, setRevision] = useState(0);
  const { downloads, loading: downloadsLoading, error: downloadsError } = useDownloads(auth.token, revision, auth.unauthorized);
  const [modal, setModal] = useState<'login' | 'create' | null>(null);
  const [deleteFile, setDeleteFile] = useState<LibraryFile | null>(null);
  const [cancelDownload, setCancelDownload] = useState<Download | null>(null);
  const [history, setHistory] = useState<Download | null>(null);
  const [pending, setPending] = useState<string | null>(null);
  const [notice, setNotice] = useState('');
  const [actionError, setActionError] = useState('');

  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    async function load() {
      setLibraryLoading(true);
      try { const result = await api.library(controller.signal); if (active) { setFiles(result); setLibraryError(false); } }
      catch { if (active) setLibraryError(true); }
      finally { if (active) setLibraryLoading(false); }
    }
    function visibility() { if (!document.hidden) void load(); }
    void load();
    document.addEventListener('visibilitychange', visibility);
    return () => { active = false; controller.abort(); document.removeEventListener('visibilitychange', visibility); };
  }, [revision]);

  useEffect(() => {
    if (!auth.token) { setDeleteFile(null); setCancelDownload(null); setHistory(null); setActionError(''); if (modal === 'create') setModal(null); }
  }, [auth.token, modal]);

  async function act(operation: () => Promise<unknown>, id: string, success: string) {
    const token = auth.token;
    if (!token || pending) return;
    setPending(id); setActionError(''); setNotice('');
    try {
      await operation();
      if (currentToken.current !== token) return;
      setNotice(success); setDeleteFile(null); setCancelDownload(null); setRevision(value => value + 1);
    } catch (error) {
      auth.unauthorized(error, token);
      if (currentToken.current === token) setActionError(errorLabel(error instanceof ApiError ? error.code : 'request_failed'));
    } finally { setPending(null); }
  }

  const activeCount = downloads.filter(download => ['Queued', 'Downloading', 'Preparing', 'Uploading', 'CleaningUp', 'Canceling', 'Deleting'].includes(download.state)).length;
  const closeConfirmation = () => { if (!pending) { setDeleteFile(null); setCancelDownload(null); setActionError(''); } };

  return <>
    <header className="app-header">
      <a className="brand" href="/" aria-label={messages.appTitle}><span className="brand-icon"><Icon name="download" /></span><span><strong>{messages.brand}<span className="brand-dot">.</span></strong><span className="brand-caption">{messages.brandCaption}</span></span></a>
      <div className="auth-actions">{auth.checking ? <span className="session-check">{messages.checkingSession}</span> : auth.token ? <><span className="admin-label"><span className="online-dot" />{messages.admin}</span><button className="button outlined" onClick={() => void act(auth.logout, 'logout', '')} disabled={pending === 'logout'}><Icon name="logout" /><span>{messages.logout}</span></button></> : <button className="button outlined" onClick={() => setModal('login')}><Icon name="login" />{messages.login}</button>}</div>
    </header>

    <main className="app-main">
      <div className="demo-banner"><Icon name="info" /><div><strong>{messages.demo}</strong><p>{messages.demoDescription}</p></div></div>
      {auth.expired && <div className="session-banner" role="status">{messages.sessionExpired}<button className="button text-button" onClick={() => setModal('login')}>{messages.login}</button></div>}
      {notice && <div className="notice" role="status"><Icon name="check" />{notice}<button className="icon-button" aria-label={messages.close} onClick={() => setNotice('')}><Icon name="close" /></button></div>}
      {actionError && !deleteFile && !cancelDownload && <div className="form-error" role="alert">{actionError}</div>}

      {auth.token && <section className="downloads-section" aria-labelledby="downloads-title">
        <div className="section-heading"><div><p className="eyebrow">{messages.queueCount.replace('{count}', formatNumber(activeCount))}</p><h2 id="downloads-title">{messages.downloads}</h2><p>{messages.downloadsCaption}</p></div><button className="button primary" onClick={() => { setActionError(''); setModal('create'); }}><Icon name="plus" />{messages.newDownload}</button></div>
        {downloadsError && <div className="load-error" role="alert">{messages.errors.request_failed}<button className="button text-button" onClick={() => setRevision(value => value + 1)}>{messages.retryLoad}</button></div>}
        {downloadsLoading && downloads.length === 0 ? <div className="loading-state" role="status">{messages.loading}</div> : downloads.length === 0 && !downloadsError ? <div className="empty-state"><Icon name="plus" /><h3>{messages.emptyDownloads}</h3><p>{messages.emptyDownloadsDescription}</p></div> : <div className="download-grid">{downloads.map(download => <DownloadCard key={download.id} download={download} pending={pending === download.id} onHistory={() => setHistory(download)} onCancel={() => { setActionError(''); setCancelDownload(download); }} onRetry={() => void act(() => api.act(download.id, 'retry', auth.token!), download.id, messages.downloadRetried)} />)}</div>}
      </section>}

      <section className="library-section" aria-labelledby="library-title">
        <div className="section-heading library-heading"><div><p className="eyebrow">{messages.fileCount.replace('{count}', formatNumber(files.length))}</p><h1 id="library-title">{messages.library}</h1><p>{messages.libraryCaption}</p></div><button className="icon-button refresh-button" aria-label={messages.refresh} onClick={() => setRevision(value => value + 1)} disabled={libraryLoading}><Icon name="retry" /></button></div>
        <div className="library-panel"><div className="library-summary"><span><Icon name="library" />{messages.filesLabel}<span className="count-pill">{formatNumber(files.length)}</span></span><span>{messages.totalSize}<strong>{formatSize(files.reduce((sum, file) => sum + file.size, 0))}</strong></span></div>
          {libraryError ? <div className="empty-state" role="alert"><Icon name="info" /><p>{messages.errors.request_failed}</p><button className="button tonal" onClick={() => setRevision(value => value + 1)}>{messages.retryLoad}</button></div> : libraryLoading && files.length === 0 ? <div className="loading-state" role="status">{messages.loading}</div> : files.length === 0 ? <div className="empty-state"><Icon name="library" /><h3>{messages.emptyLibrary}</h3><p>{messages.emptyLibraryDescription}</p></div> : <LibraryList files={files} admin={!!auth.token} onDelete={file => { setActionError(''); setDeleteFile(file); }} />}
        </div>
      </section>
    </main>
    <footer className="app-footer"><span>{messages.brand}</span><span>{messages.brandCaption}</span></footer>

    {modal === 'login' && <LoginDialog onClose={() => setModal(null)} login={auth.login} />}
    {modal === 'create' && auth.token && <NewDownloadDialog token={auth.token} onClose={() => setModal(null)} unauthorized={auth.unauthorized} onCreated={() => { if (!currentToken.current) return; setModal(null); setNotice(messages.downloadCreated); setRevision(value => value + 1); }} />}
    {history && auth.token && <HistoryDialog download={history} onClose={() => setHistory(null)} />}
    {(deleteFile || cancelDownload) && auth.token && <Dialog title={deleteFile ? messages.deleteTitle : messages.cancelTitle} onClose={closeConfirmation}>
      <p className="dialog-description">{deleteFile ? messages.deleteDescription : messages.cancelDescription}</p>
      <p className="confirmation-name">{deleteFile?.name ?? cancelDownload?.title ?? cancelDownload?.url}</p>
      {actionError && <p className="form-error" role="alert">{actionError}</p>}
      <div className="dialog-actions"><button className="button text-button" disabled={!!pending} onClick={closeConfirmation}>{messages.cancel}</button><button className="button danger" disabled={!!pending} onClick={() => void (deleteFile ? act(() => api.deleteFile(deleteFile.id, auth.token!), deleteFile.id, messages.fileRemoved) : act(() => api.act(cancelDownload!.id, 'cancel', auth.token!), cancelDownload!.id, messages.downloadCanceled))}><Icon name={deleteFile ? 'trash' : 'close'} />{pending ? messages.working : deleteFile ? messages.delete : messages.cancelDownload}</button></div>
    </Dialog>}
  </>;
}
