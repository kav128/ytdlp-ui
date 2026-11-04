import {useEffect, useId, useRef, type ReactNode} from 'react';
import Icon from './Icon';
import {messages} from '../locales';

export default function Dialog({title, onClose, children, className = '', busy = false}: {
    title: string; onClose: () => void; children: ReactNode; className?: string; busy?: boolean;
}) {
    const dialog = useRef<HTMLDialogElement>(null);
    const titleId = useId();
    const previousFocus = useRef(document.activeElement);
    useEffect(() => {
        const element = dialog.current!;
        element.showModal();
        element.querySelector('input')?.focus();
        return () => {
            element.close();
            if (previousFocus.current instanceof HTMLElement) previousFocus.current.focus();
        };
    }, []);
    return <dialog ref={dialog} className={`dialog ${className}`} aria-labelledby={titleId} aria-busy={busy}
                   onCancel={event => {
                       event.preventDefault();
                       onClose();
                   }}
                   onClick={event => {
                       if (event.target === event.currentTarget) {
                           const rect = event.currentTarget.getBoundingClientRect();
                           if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) onClose();
                       }
                   }}>
        <div className="dialog-heading"><h2 id={titleId}>{title}</h2>
            <button className="icon-button" onClick={onClose} disabled={busy} aria-label={messages.close}><Icon
                name="close"/></button>
        </div>
        {children}
    </dialog>;
}
