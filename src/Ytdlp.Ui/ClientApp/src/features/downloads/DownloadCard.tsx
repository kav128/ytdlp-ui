import type { Download } from '../../api/client';
import Icon from '../../components/Icon';
import { messages } from '../../locales';
import { errorLabel, formatDate, stateLabel } from '../../locales/format';

export default function DownloadCard({ download, pending, onRetry, onCancel, onHistory }: {
  download: Download; pending: boolean; onRetry: () => void; onCancel: () => void; onHistory: () => void;
}) {
  const active = ['Downloading', 'Preparing', 'Uploading', 'CleaningUp', 'Deleting', 'Canceling'].includes(download.state);
  return <article className={`download-card ${download.state === 'Failed' ? 'has-error' : ''}`}>
    <div className="card-heading"><span className={`status status-${download.state.toLowerCase()}`}><span className="status-dot" />{stateLabel(download.state)}</span><span className="attempt-label">{messages.attempt} {download.attempts.length}</span></div>
    <h3>{download.title ?? download.url}</h3>
    {download.title && <p className="source-url" title={download.url}>{download.url}</p>}
    <p className="card-date"><Icon name="clock" />{formatDate(download.createdAt)}</p>
    {active && <div className={`progress ${download.progress === null ? 'indeterminate' : ''}`} role="progressbar" aria-label={stateLabel(download.state)} aria-valuemin={download.progress === null ? undefined : 0} aria-valuemax={download.progress === null ? undefined : 100} aria-valuenow={download.progress ?? undefined}><span style={download.progress === null ? undefined : { width: `${download.progress}%` }} /></div>}
    {active && download.progress !== null && <p className="progress-caption">{new Intl.NumberFormat(document.documentElement.lang, { style: 'percent', maximumFractionDigits: 0 }).format(download.progress / 100)}</p>}
    {download.errorCode && <div className="download-error"><Icon name="info" /><div><p>{errorLabel(download.errorCode)}</p>{download.error && <details><summary>{messages.errorDetails}</summary><p>{download.error}</p></details>}</div></div>}
    <div className="card-actions">
      <button className="button text-button history-button" onClick={onHistory}>{messages.history}</button>
      {download.allowedActions.includes('cancel') && <button className="button text-button danger-text" onClick={onCancel} disabled={pending}>{messages.cancelDownload}</button>}
      {download.allowedActions.includes('retry') && <button className="button tonal" onClick={onRetry} disabled={pending}><Icon name="retry" />{pending ? messages.working : messages.retry}</button>}
    </div>
  </article>;
}
