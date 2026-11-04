import {useLayoutEffect, useRef, useState} from 'react';
import {api, ApiError, type Download, type LibraryFile} from './api/client';
import Dialog from './components/Dialog';
import Icon from './components/Icon';
import {useAuth} from './features/auth/useAuth';
import DownloadCard from './features/downloads/DownloadCard';
import HistoryDialog from './features/downloads/HistoryDialog';
import NewDownloadDialog from './features/downloads/NewDownloadDialog';
import {useDownloads} from './features/downloads/useDownloads';
import EditFileTitleDialog from './features/library/EditFileTitleDialog';
import LibraryList from './features/library/LibraryList';
import UploadDialog from './features/library/UploadDialog';
import {useLibrary} from './features/library/useLibrary';
import {messages} from './locales';
import {errorLabel, formatNumber, formatSize} from './locales/format';

export default function App() {
    const auth = useAuth();
    const currentSession = useRef(auth.sessionId);
    currentSession.current = auth.sessionId;
    const [revision, setRevision] = useState(0);
    const {
        downloads,
        loading: downloadsLoading,
        error: downloadsError
    } = useDownloads(auth.sessionId, revision, auth.authorized, auth.unauthorized);
    const publications = downloads.filter(download => ['Available', 'Deleted'].includes(download.publicationState))
        .map(download => `${download.id}:${download.publicationId}:${download.publicationState}`).sort().join('|');
    const {files, loading: libraryLoading, error: libraryError} = useLibrary(`${revision}|${publications}`);
    const [modal, setModal] = useState<'create' | 'upload' | null>(null);
    const [deleteFile, setDeleteFile] = useState<LibraryFile | null>(null);
    const [editFile, setEditFile] = useState<LibraryFile | null>(null);
    const [cancelId, setCancelId] = useState<string | null>(null);
    const cancelDownload = downloads.find(download => download.id === cancelId);
    const [historyId, setHistoryId] = useState<string | null>(null);
    const history = downloads.find(download => download.id === historyId);
    const [pending, setPending] = useState<string | null>(null);
    const [notice, setNotice] = useState('');
    const [actionError, setActionError] = useState('');

    useLayoutEffect(() => {
        setDeleteFile(null);
        setEditFile(null);
        setCancelId(null);
        setHistoryId(null);
        setActionError('');
        setPending(null);
        setModal(null);
        setNotice('');
    }, [auth.sessionId]);

    async function act<T>(operation: () => Promise<T>, id: string, success: string | ((result: T) => string)) {
        const token = auth.sessionId;
        if (!token || pending) return;
        setPending(id);
        setActionError('');
        setNotice('');
        try {
            const result = await operation();
            if (currentSession.current !== token) return;
            setNotice(typeof success === 'function' ? success(result) : success);
            setDeleteFile(null);
            setEditFile(null);
            setCancelId(null);
            setRevision(value => value + 1);
        } catch (error) {
            auth.unauthorized(error, token);
            if (currentSession.current === token) setActionError(errorLabel(error instanceof ApiError ? error.code : 'request_failed'));
        } finally {
            if (currentSession.current === token) setPending(null);
        }
    }

    const activeCount = downloads.filter(download => ['Queued', 'Downloading', 'Preparing', 'Uploading', 'CleaningUp', 'Canceling', 'Deleting'].includes(download.state)).length;
    const closeConfirmation = () => {
        if (!pending) {
            setDeleteFile(null);
            setEditFile(null);
            setCancelId(null);
            setActionError('');
        }
    };

    return <>
        <header className="app-header">
            <a className="brand" href="/" aria-label={messages.appTitle}><span className="brand-icon"><Icon
                name="download"/></span><span><strong>{messages.brand}<span className="brand-dot">.</span></strong><span
                className="brand-caption">{messages.brandCaption}</span></span></a>
            <div className="auth-actions">{auth.checking ?
                <span className="session-check">{messages.checkingSession}</span> : auth.sessionId ? <><span
                    className="admin-label"><span className="online-dot"/>{messages.admin}</span>
                    <button className="button outlined" onClick={() => void act(auth.logout, 'logout', '')}
                            disabled={pending === 'logout'}><Icon name="logout"/><span>{messages.logout}</span></button>
                </> : <button className="button outlined" onClick={auth.login}><Icon name="login"/>{messages.login}
                </button>}</div>
        </header>

        <main className="app-main">
            {auth.loginError && <div className="form-error" role="alert">{errorLabel(auth.loginError)}</div>}
            {auth.expired && <div className="session-banner" role="status">{messages.sessionExpired}
                <button className="button text-button" onClick={auth.login}>{messages.login}</button>
            </div>}
            {notice && <div className="notice" role="status"><Icon name="check"/>{notice}
                <button className="icon-button" aria-label={messages.close} onClick={() => setNotice('')}><Icon
                    name="close"/></button>
            </div>}
            {actionError && !deleteFile && !cancelDownload &&
                <div className="form-error" role="alert">{actionError}</div>}

            {auth.sessionId && <section className="downloads-section" aria-labelledby="downloads-title">
                <div className="section-heading">
                    <div><p className="eyebrow">{messages.queueCount.replace('{count}', formatNumber(activeCount))}</p>
                        <h2 id="downloads-title">{messages.downloads}</h2><p>{messages.downloadsCaption}</p></div>
                    <button className="button primary" onClick={() => {
                        setActionError('');
                        setModal('create');
                    }}><Icon name="plus"/>{messages.newDownload}</button>
                </div>
                {downloadsError && <div className="load-error" role="alert">{messages.errors.request_failed}
                    <button className="button text-button"
                            onClick={() => setRevision(value => value + 1)}>{messages.retryLoad}</button>
                </div>}
                {downloadsLoading && downloads.length === 0 ? <div className="loading-state"
                                                                   role="status">{messages.loading}</div> : downloads.length === 0 && !downloadsError ?
                    <div className="empty-state"><Icon name="plus"/><h3>{messages.emptyDownloads}</h3>
                        <p>{messages.emptyDownloadsDescription}</p></div> :
                    <div className="download-grid">{downloads.map(download => <DownloadCard key={download.id}
                                                                                            download={download}
                                                                                            pending={pending === download.id}
                                                                                            onHistory={() => setHistoryId(download.id)}
                                                                                            onCancel={() => {
                                                                                                setActionError('');
                                                                                                setCancelId(download.id);
                                                                                            }}
                                                                                            onRetry={() => void act(() => auth.authorized(token => api.act(download.id, 'retry', token), auth.sessionId!), download.id, messages.downloadRetried)}/>)}</div>}
            </section>}

            <section className="library-section" aria-labelledby="library-title">
                <div className="section-heading library-heading">
                    <div><p className="eyebrow">{messages.fileCount.replace('{count}', formatNumber(files.length))}</p>
                        <h1 id="library-title">{messages.library}</h1><p>{messages.libraryCaption}</p></div>
                    <div className="library-actions">
                        {auth.sessionId && <button className="button primary"
                                                   onClick={() => setModal('upload')}>{messages.uploadFile}</button>}
                        <button className="icon-button refresh-button" aria-label={messages.refresh}
                                onClick={() => setRevision(value => value + 1)} disabled={libraryLoading}><Icon
                            name="retry"/></button>
                    </div>
                </div>
                <div className="library-panel">
                    <div className="library-summary"><span><Icon name="library"/>{messages.filesLabel}<span
                        className="count-pill">{formatNumber(files.length)}</span></span><span>{messages.totalSize}<strong>{formatSize(files.reduce((sum, file) => sum + file.size, 0))}</strong></span>
                    </div>
                    {libraryError && <div className="load-error" role="alert"><Icon name="info"/>
                        <p>{messages.errors.request_failed}</p>
                        <button className="button tonal"
                                onClick={() => setRevision(value => value + 1)}>{messages.retryLoad}</button>
                    </div>}
                    {libraryLoading && files.length === 0 ?
                        <div className="loading-state"
                             role="status">{messages.loading}</div> : files.length === 0 && !libraryError ?
                            <div className="empty-state"><Icon name="library"/><h3>{messages.emptyLibrary}</h3>
                                <p>{messages.emptyLibraryDescription}</p></div> :
                            files.length > 0 && <LibraryList files={files} admin={!!auth.sessionId} onEdit={file => {
                                setActionError('');
                                setEditFile(file);
                            }} onDelete={file => {
                                setActionError('');
                                setDeleteFile(file);
                            }}/>}
                </div>
            </section>
        </main>
        <footer className="app-footer"><span>{messages.brand}</span><span>{messages.brandCaption}</span></footer>

        {modal === 'create' && auth.sessionId &&
            <NewDownloadDialog key={auth.sessionId} sessionId={auth.sessionId} authorized={auth.authorized}
                               onClose={() => setModal(null)}
                               unauthorized={auth.unauthorized} onCreated={() => {
                if (currentSession.current !== auth.sessionId) return;
                setModal(null);
                setNotice(messages.downloadCreated);
                setRevision(value => value + 1);
            }}/>}
        {history && auth.sessionId &&
            <HistoryDialog download={history} error={downloadsError} onClose={() => setHistoryId(null)}/>}
        {modal === 'upload' && auth.sessionId && <UploadDialog key={auth.sessionId} sessionId={auth.sessionId}
                                                               authorized={auth.authorized}
                                                               unauthorized={auth.unauthorized}
                                                               onClose={() => setModal(null)} onUploaded={() => {
            if (currentSession.current !== auth.sessionId) return;
            setModal(null);
            setNotice(messages.fileUploaded);
            setRevision(value => value + 1);
        }}/>}
        {editFile && auth.sessionId && <EditFileTitleDialog key={editFile.id} file={editFile} busy={!!pending}
                                                            error={actionError} onClose={closeConfirmation}
                                                            onSave={title => void act(
                                                                () => auth.authorized(token => api.setFileTitle(editFile.id, title, token), auth.sessionId!),
                                                                editFile.id,
                                                                title === null ? messages.fileTitleRemoved : messages.fileTitleSaved
                                                            )}/>}
        {(deleteFile || cancelDownload) && auth.sessionId &&
            <Dialog title={deleteFile ? messages.deleteTitle : messages.cancelTitle} onClose={closeConfirmation}
                    busy={!!pending}>
                <p className="dialog-description">{deleteFile ? messages.deleteDescription : messages.cancelDescription}</p>
                <p className="confirmation-name">{deleteFile?.title ?? deleteFile?.name ?? cancelDownload?.title ?? cancelDownload?.url}</p>
                {actionError && <p className="form-error" role="alert">{actionError}</p>}
                {cancelDownload && !cancelDownload.allowedActions.includes('cancel') &&
                    <p className="form-error" role="alert">{messages.errors.invalid_state}</p>}
                <div className="dialog-actions">
                    <button className="button text-button" disabled={!!pending}
                            onClick={closeConfirmation}>{messages.cancel}</button>
                    <button className="button danger"
                            disabled={!!pending || !!cancelDownload && !cancelDownload.allowedActions.includes('cancel')}
                            onClick={() => void (deleteFile ? act(() => auth.authorized(token => api.deleteFile(deleteFile.id, token), auth.sessionId!), deleteFile.id, result => result?.downloadId ? messages.fileDeleteQueued : messages.fileRemoved) : act(() => auth.authorized(token => api.act(cancelDownload!.id, 'cancel', token), auth.sessionId!), cancelDownload!.id, messages.downloadCanceled))}>
                        <Icon
                            name={deleteFile ? 'trash' : 'close'}/>{pending ? messages.working : deleteFile ? messages.delete : messages.cancelDownload}
                    </button>
                </div>
            </Dialog>}
    </>;
}
