import type { LibraryFile } from '../../api/client';
import Icon from '../../components/Icon';
import { messages } from '../../locales';
import { formatDate, formatSize } from '../../locales/format';

export default function LibraryList({ files, admin, onDelete }: { files: LibraryFile[]; admin: boolean; onDelete: (file: LibraryFile) => void }) {
  return <ul className="file-list">{files.map((file, index) => <li className="file-row" key={file.id}>
    <div className={`file-symbol color-${index % 3}`}><Icon name="file" /></div>
    <div className="file-info"><h3>{file.name}</h3><div className="file-meta"><span>{formatSize(file.size)}</span><span className="meta-divider" /><time dateTime={file.lastModified}>{formatDate(file.lastModified)}</time></div></div>
    <div className="file-actions">{admin && <button className="icon-button delete-button" aria-label={`${messages.delete}: ${file.name}`} onClick={() => onDelete(file)}><Icon name="trash" /></button>}<a className="button tonal download-button" href={`/api/library/${encodeURIComponent(file.id)}/content`} download><Icon name="download" /><span>{messages.download}</span><span className="sr-only">: {file.name}</span></a></div>
  </li>)}</ul>;
}
