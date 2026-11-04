import {useEffect, useId, useRef, useState, type FormEvent} from 'react';
import Dialog from '../../components/Dialog';
import {api, ApiError} from '../../api/client';
import {messages} from '../../locales';
import {errorLabel} from '../../locales/format';
import type {AuthenticatedRequest} from '../auth/useAuth';

export default function UploadDialog({sessionId, authorized, unauthorized, onClose, onUploaded}: {
    sessionId: string; authorized: AuthenticatedRequest; unauthorized: (error: unknown, session: string) => void;
    onClose: () => void; onUploaded: () => void;
}) {
    const [file, setFile] = useState<File | null>(null);
    const [pending, setPending] = useState(false);
    const [error, setError] = useState('');
    const active = useRef(true);
    const sending = useRef(false);
    const controller = useRef<AbortController | null>(null);
    const input = useRef<HTMLInputElement>(null);
    const fileId = useId(), nameId = useId();
    useEffect(() => {
        active.current = true;
        return () => {
            active.current = false;
            controller.current?.abort();
        };
    }, []);

    async function submit(event: FormEvent) {
        event.preventDefault();
        if (!file || sending.current) return;
        sending.current = true;
        setPending(true);
        setError('');
        controller.current = new AbortController();
        try {
            await authorized(csrf => api.upload(file, csrf, controller.current!.signal), sessionId);
            if (active.current) onUploaded();
        } catch (error) {
            unauthorized(error, sessionId);
            if (active.current) setError(errorLabel(error instanceof ApiError ? error.code : 'request_failed'));
        } finally {
            sending.current = false;
            if (active.current) setPending(false);
        }
    }

    return <Dialog title={messages.uploadTitle} busy={pending} onClose={() => {
        if (!pending) onClose();
    }}>
        <p className="dialog-description">{messages.uploadDescription}</p>
        <form onSubmit={submit}>
            <div className="field">
                <label htmlFor={fileId}>{messages.chooseFile}</label>
                <input ref={input} id={fileId} type="file" hidden required disabled={pending}
                       onChange={event => setFile(event.target.files?.[0] ?? null)}/>
                <div className="file-picker">
                    <button type="button" className="button tonal" autoFocus disabled={pending}
                            aria-describedby={nameId}
                            onClick={() => input.current?.click()}>{messages.selectFile}</button>
                    <span id={nameId} className="selected-file-name"
                          aria-live="polite">{file?.name ?? messages.noFileSelected}</span>
                </div>
            </div>
            {pending && <p role="status">{messages.uploadingFile}</p>}
            {error && <p className="form-error" role="alert">{error}</p>}
            <div className="dialog-actions">
                <button type="button" className="button text-button" disabled={pending}
                        onClick={onClose}>{messages.cancel}</button>
                <button className="button primary" disabled={!file || pending}>{messages.uploadFile}</button>
            </div>
        </form>
    </Dialog>;
}
