import { useState, type FormEvent } from 'react';
import Dialog from '../../components/Dialog';
import Icon from '../../components/Icon';
import { ApiError } from '../../api/client';
import { messages } from '../../locales';
import { errorLabel } from '../../locales/format';

export default function LoginDialog({ onClose, login }: { onClose: () => void; login: (username: string, password: string) => Promise<void> }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  async function submit(event: FormEvent) {
    event.preventDefault(); setError('');
    if (!username.trim() || !password) { setError(errorLabel('credentials_required')); return; }
    setPending(true);
    try { await login(username.trim(), password); setPassword(''); onClose(); }
    catch (error) { setError(errorLabel(error instanceof ApiError && error.status === 401 ? 'invalid_credentials' : 'request_failed')); }
    finally { setPending(false); }
  }
  return <Dialog title={messages.loginTitle} onClose={() => { if (!pending) onClose(); }} className="login-dialog">
    <p className="dialog-description">{messages.loginDescription}</p>
    <form onSubmit={submit} noValidate>
      <label className="field">{messages.username}<input autoFocus autoComplete="username" value={username} onChange={event => setUsername(event.target.value)} required disabled={pending} /></label>
      <label className="field">{messages.password}<input type="password" autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} required disabled={pending} /></label>
      {error && <p role="alert" className="form-error">{error}</p>}
      <div className="dialog-actions"><button type="button" className="button text-button" onClick={onClose} disabled={pending}>{messages.cancel}</button><button className="button primary" disabled={pending}><Icon name="login" />{pending ? messages.working : messages.login}</button></div>
    </form>
  </Dialog>;
}
