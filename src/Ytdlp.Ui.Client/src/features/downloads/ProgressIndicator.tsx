import {formatPercent} from '../../locales/format';

export default function ProgressIndicator({value, label}: { value: number | null; label: string }) {
    const known = value !== null && Number.isFinite(value) && value >= 0 && value <= 100;
    return <div className="progress-display">
        <div className={`progress ${known ? '' : 'indeterminate'}`} role="progressbar" aria-label={label}
             aria-valuemin={known ? 0 : undefined} aria-valuemax={known ? 100 : undefined}
             aria-valuenow={known ? value : undefined} aria-valuetext={known ? formatPercent(value) : undefined}>
            <span style={known ? {width: `${value}%`} : undefined}/>
        </div>
        {known && <p className="progress-caption">{formatPercent(value)}</p>}
    </div>;
}
