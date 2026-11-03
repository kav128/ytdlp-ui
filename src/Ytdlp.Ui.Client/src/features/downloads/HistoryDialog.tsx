import type {Download} from '../../api/client';
import Dialog from '../../components/Dialog';
import {messages} from '../../locales';
import {
    errorLabel,
    explanationLabel,
    formatDate,
    formatNumber,
    publicationLabel,
    reasonLabel,
    stageLabel,
    stateLabel,
    statusLabel
} from '../../locales/format';
import ProgressIndicator from './ProgressIndicator';

function TimeValue({value}: { value: string }) {
    return <time dateTime={value}>{formatDate(value)}</time>;
}

export default function HistoryDialog({download, error, onClose}: {
    download: Download;
    error: boolean;
    onClose: () => void
}) {
    const origins = new Map(download.attempts.flatMap(attempt => attempt.stages.map(stage => [stage.id, attempt.number] as const)));
    return <Dialog title={messages.historyTitle} onClose={onClose} className="history-dialog">
        <p className="history-name">{download.title ?? download.url}</p>
        <dl className="history-facts">
            <div>
                <dt>{messages.currentState}</dt>
                <dd>{stateLabel(download.state)}</dd>
            </div>
            {download.failedState && <div>
                <dt>{messages.failedStage}</dt>
                <dd>{stateLabel(download.failedState)}</dd>
            </div>}
            {download.resumeState && <div>
                <dt>{messages.resumeFrom}</dt>
                <dd>{stateLabel(download.resumeState)}</dd>
            </div>}
            <div>
                <dt>{messages.publication}</dt>
                <dd>{publicationLabel(download.publicationState)}</dd>
            </div>
        </dl>
        {error && <p className="form-error" role="alert">{messages.historyRefreshFailed}</p>}
        <ol className="attempts">{[...download.attempts].reverse().map(attempt => <li key={attempt.id}
                                                                                      className="attempt-item">
            <div className="attempt-marker">{formatNumber(attempt.number)}</div>
            <div className="attempt-content">
                <div className="attempt-heading"><h3>{messages.attempt} {formatNumber(attempt.number)}</h3>
                    <span
                        className={`execution-status status-${attempt.status.toLowerCase()}`}>{statusLabel(attempt.status)}</span>
                </div>
                <p className="attempt-reason">{reasonLabel(attempt.reason)}</p>
                <dl className="history-facts compact">
                    <div>
                        <dt>{messages.currentState}</dt>
                        <dd>{stateLabel(attempt.state)}</dd>
                    </div>
                    <div>
                        <dt>{messages.resumeFrom}</dt>
                        <dd>{stateLabel(attempt.initialState)}</dd>
                    </div>
                    <div>
                        <dt>{messages.queuedAt}</dt>
                        <dd><TimeValue value={attempt.createdAt}/></dd>
                    </div>
                    {attempt.startedAt && <div>
                        <dt>{messages.startedAt}</dt>
                        <dd><TimeValue value={attempt.startedAt}/></dd>
                    </div>}
                    {attempt.finishedAt && <div>
                        <dt>{messages.finishedAt}</dt>
                        <dd><TimeValue value={attempt.finishedAt}/></dd>
                    </div>}
                </dl>
                {attempt.continuationReasonCode &&
                    <p className="history-explanation">{explanationLabel(attempt.continuationReasonCode) ?? messages.continuationFallback}</p>}
                {attempt.errorCode && <p className="history-error">{errorLabel(attempt.errorCode)}</p>}
                {attempt.error && <details className="diagnostics">
                    <summary>{messages.errorDetails}</summary>
                    <p>{attempt.error}</p></details>}
                <ol className="stage-list" aria-label={messages.stagesTitle}>{attempt.stages.map(stage => <li
                    key={stage.id} className={`stage-row stage-${stage.status.toLowerCase()}`}>
                    <div className="stage-heading"><h4>{stageLabel(stage.stage)}</h4><span
                        className={`execution-status status-${stage.status.toLowerCase()}`}>{statusLabel(stage.status)}</span>
                    </div>
                    {stage.reusedFromStageId && <p className="stage-reuse">{origins.has(stage.reusedFromStageId)
                        ? messages.reusedFromAttempt.replace('{number}', formatNumber(origins.get(stage.reusedFromStageId)!)) : messages.reusedResult}</p>}
                    {stage.status === 'Running' &&
                        <ProgressIndicator value={stage.progress} label={stageLabel(stage.stage)}/>}
                    <dl className="history-facts compact stage-times">
                        {stage.startedAt && <div>
                            <dt>{messages.startedAt}</dt>
                            <dd><TimeValue value={stage.startedAt}/></dd>
                        </div>}
                        {stage.finishedAt && <div>
                            <dt>{messages.finishedAt}</dt>
                            <dd><TimeValue value={stage.finishedAt}/></dd>
                        </div>}
                    </dl>
                    {stage.errorCode &&
                        <p className={stage.status === 'Failed' ? 'history-error' : 'history-explanation'}>{explanationLabel(stage.errorCode) ?? errorLabel(stage.errorCode)}</p>}
                    {stage.error && <details className="diagnostics">
                        <summary>{messages.errorDetails}</summary>
                        <p>{stage.error}</p></details>}
                    {stage.artifactId && <p className="stage-result">{messages.stageResultRecorded}</p>}
                </li>)}</ol>
            </div>
        </li>)}</ol>
        <div className="dialog-actions">
            <button className="button tonal" onClick={onClose}>{messages.close}</button>
        </div>
    </Dialog>;
}
