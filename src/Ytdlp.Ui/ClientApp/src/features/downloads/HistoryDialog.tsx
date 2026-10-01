import type { Download } from '../../api/client';
import Dialog from '../../components/Dialog';
import { messages } from '../../locales';
import { formatDate, stateLabel } from '../../locales/format';

export default function HistoryDialog({ download, onClose }: { download: Download; onClose: () => void }) {
  return <Dialog title={messages.historyTitle} onClose={onClose}>
    <p className="history-name">{download.title ?? download.url}</p>
    {download.resumeState && <p className="field-hint">{messages.resumeFrom}: {stateLabel(download.resumeState)}</p>}
    <ol className="attempts">{[...download.attempts].reverse().map(attempt => <li key={attempt.number}>
      <div className="attempt-marker">{attempt.number}</div><div><h3>{messages.attempt} {attempt.number}</h3><p>{stateLabel(attempt.state)}</p><time dateTime={attempt.createdAt}>{formatDate(attempt.createdAt)}</time>{attempt.error && <details><summary>{messages.errorDetails}</summary><p>{attempt.error}</p></details>}</div>
    </li>)}</ol>
    <div className="dialog-actions"><button className="button tonal" onClick={onClose}>{messages.close}</button></div>
  </Dialog>;
}
