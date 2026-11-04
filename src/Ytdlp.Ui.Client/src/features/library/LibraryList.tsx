import type {LibraryFile} from '../../api/client';
import Icon from '../../components/Icon';
import {messages} from '../../locales';
import {formatDate, formatSize} from '../../locales/format';

export default function LibraryList({files, admin, onEdit, onDelete}: {
    files: LibraryFile[]; admin: boolean; onEdit: (file: LibraryFile) => void; onDelete: (file: LibraryFile) => void;
}) {
    return <ul className="file-list">{files.map((file, index) => {
        const label = file.title ?? file.name;
        return <li className="file-row" key={file.id}>
            <div className={`file-symbol color-${index % 3}`}><Icon name="file"/></div>
            <div className="file-info">
                <h3>{label}</h3>
                <div className="file-meta">
                    <span>{formatSize(file.size)}</span><span className="meta-divider"/>
                    <time dateTime={file.lastModified}>{formatDate(file.lastModified)}</time>
                </div>
            </div>
            <div className="file-actions">
                {admin && <>
                    <button className="icon-button" aria-label={`${messages.editFileTitle}: ${label}`}
                            onClick={() => onEdit(file)}><Icon name="edit"/></button>
                    <button className="icon-button delete-button" aria-label={`${messages.delete}: ${label}`}
                            onClick={() => onDelete(file)}><Icon name="trash"/></button>
                </>}
                <a className="button tonal download-button" href={`/api/library/${encodeURIComponent(file.id)}/content`}
                   download>
                    <Icon name="download"/><span>{messages.download}</span><span className="sr-only">: {label}</span>
                </a>
            </div>
        </li>;
    })}</ul>;
}
