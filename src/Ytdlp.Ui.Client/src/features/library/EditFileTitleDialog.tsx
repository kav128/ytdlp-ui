import {useId, useState} from 'react';
import type {LibraryFile} from '../../api/client';
import Dialog from '../../components/Dialog';
import {messages} from '../../locales';

export default function EditFileTitleDialog({file, busy, error, onClose, onSave}: {
    file: LibraryFile; busy: boolean; error: string; onClose: () => void; onSave: (title: string | null) => void;
}) {
    const [title, setTitle] = useState(file.title ?? '');
    const titleId = useId();

    return <Dialog title={messages.editFileTitle} busy={busy} onClose={onClose}>
        <p className="dialog-description">{messages.originalFileName}</p>
        <p className="confirmation-name">{file.name}</p>
        <form onSubmit={event => {
            event.preventDefault();
            if (!busy) onSave(title.trim() || null);
        }}>
            <div className="field">
                <label htmlFor={titleId}>{messages.fileTitle}</label>
                <input id={titleId} autoFocus disabled={busy} maxLength={255} value={title}
                       onChange={event => setTitle(event.target.value)}/>
            </div>
            {error && <p className="form-error" role="alert">{error}</p>}
            <div className="dialog-actions">
                <button type="button" className="button text-button" disabled={busy}
                        onClick={onClose}>{messages.cancel}</button>
                {file.title && <button type="button" className="button text-button" disabled={busy}
                                       onClick={() => onSave(null)}>{messages.removeFileTitle}</button>}
                <button className="button primary"
                        disabled={busy}>{busy ? messages.working : messages.saveFileTitle}</button>
            </div>
        </form>
    </Dialog>;
}
