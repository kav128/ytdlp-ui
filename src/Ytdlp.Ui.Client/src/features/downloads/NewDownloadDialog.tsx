import { useState, type FormEvent } from 'react';
import Dialog from '../../components/Dialog';
import Icon from '../../components/Icon';
import { api, ApiError } from '../../api/client';
import { messages } from '../../locales';
import { errorLabel } from '../../locales/format';

export default function NewDownloadDialog({ token, onClose, onCreated, unauthorized }: {
  token: string; onClose: () => void; onCreated: () => void; unauthorized: (error: unknown, token: string) => void;
}) {
  const [url, setUrl] = useState('');
  const [error, setError] = useState('');
  const [pending, setPending] = useState(false);
  async function submit(event: FormEvent) {
    event.preventDefault(); setError('');
    try { const parsed = new URL(url.trim()); if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password) throw new Error(); }
    catch { setError(errorLabel('invalid_url')); return; }
    setPending(true);
    try { await api.create(url.trim(), token); onCreated(); }
    catch (error) { unauthorized(error, token); setError(errorLabel(error instanceof ApiError ? error.code : 'request_failed')); }
    finally { setPending(false); }
  }
  return <Dialog title={messages.newDownloadTitle} onClose={() => { if (!pending) onClose(); }}>
    <div className="dialog-symbol"><Icon name="plus" /></div>
    <p className="dialog-description">{messages.newDownloadDescription}</p>
    <form onSubmit={submit} noValidate>
      <label className="field">{messages.url}<input autoFocus type="url" inputMode="url" autoComplete="off" placeholder="https://…" value={url} onChange={event => setUrl(event.target.value)} aria-describedby="url-hint" aria-invalid={!!error} required disabled={pending} /></label>
      <p id="url-hint" className="field-hint">{messages.urlHint}</p>
      {error && <p role="alert" className="form-error">{error}</p>}
      <div className="dialog-actions"><button type="button" className="button text-button" onClick={onClose} disabled={pending}>{messages.cancel}</button><button className="button primary" disabled={pending}><Icon name="arrow" />{pending ? messages.working : messages.submitDownload}</button></div>
    </form>
  </Dialog>;
}
