import {render, screen, within} from '@testing-library/react';
import {describe, expect, it, vi} from 'vitest';
import {messages} from '../../locales';
import {formatPercent} from '../../locales/format';
import {makeAttempt, makeDownload, makeStage} from '../../test/downloads';
import HistoryDialog from './HistoryDialog';
import DownloadCard from './DownloadCard';

describe('persisted history', () => {
    it('shows reasons, timestamps, stage status, reuse and localized continuation separately from a later failure', () => {
        const first = makeAttempt({
            errorCode: 'prepare_failed', error: 'ffmpeg stderr: bad packet',
            stages: [makeStage({
                id: 'old-download',
                stage: 'Download',
                status: 'Succeeded',
                errorCode: null,
                artifactId: 'recorded-result'
            })]
        });
        const second = makeAttempt({
            id: 'retry', number: 2, reason: 'Retry', initialState: 'Downloading', errorCode: 'download_failed',
            continuationReasonCode: 'artifacts_unavailable', error: null,
            stages: [makeStage({
                id: 'new-download',
                stage: 'Download',
                status: 'Failed',
                errorCode: 'download_failed',
                reusedFromStageId: 'old-download'
            })]
        });
        render(<HistoryDialog download={makeDownload({attempts: [first, second]})} error={false} onClose={vi.fn()}/>);
        expect(screen.getByText(messages.attemptReasons.Retry)).toBeInTheDocument();
        const reason = screen.getByText(messages.explanations.artifacts_unavailable);
        expect(reason).toHaveClass('history-explanation');
        expect(reason).not.toHaveClass('history-error');
        expect(screen.getByText(messages.reusedFromAttempt.replace('{number}', '1'))).toBeInTheDocument();
        expect(screen.getByText(messages.stageResultRecorded)).toBeInTheDocument();
        expect(screen.getByText('ffmpeg stderr: bad packet')).toBeInTheDocument();
        expect(screen.getAllByText(messages.errors.download_failed).length).toBe(2);
        expect(screen.getAllByText(messages.startedAt).length).toBeGreaterThan(0);
        expect(screen.getAllByText(messages.finishedAt).length).toBeGreaterThan(0);
    });
    it('keeps a successful continuation informational and presents a refresh failure without erasing the history', () => {
        render(<HistoryDialog download={makeDownload({
            attempts: [makeAttempt({
                status: 'Succeeded', state: 'Completed', error: null,
                errorCode: null, continuationReasonCode: 'publication_reconciliation', stages: []
            })]
        })} error={true} onClose={vi.fn()}/>);
        expect(screen.getByText(messages.explanations.publication_reconciliation)).toHaveClass('history-explanation');
        expect(screen.getByRole('alert')).toHaveTextContent(messages.historyRefreshFailed);
        expect(screen.queryByText(messages.errors.request_failed)).not.toBeInTheDocument();
    });
    it.each([null, 37])('uses only the current stage percentage, with no number for unknown progress (%s)', value => {
        render(<DownloadCard download={makeDownload({
            state: 'Downloading',
            progress: value,
            errorCode: null,
            error: null,
            allowedActions: []
        })}
                             pending={false} onRetry={vi.fn()} onCancel={vi.fn()} onHistory={vi.fn()}/>);
        const progress = screen.getByRole('progressbar', {name: messages.states.Downloading});
        if (value === null) {
            expect(progress).not.toHaveAttribute('aria-valuenow');
            expect(progress).toHaveClass('indeterminate');
            expect(screen.queryByText(/\d+%/)).not.toBeInTheDocument();
        } else {
            expect(progress).toHaveAttribute('aria-valuenow', '37');
            expect(screen.getByText(formatPercent(value))).toBeInTheDocument();
        }
        expect(screen.queryByRole('button', {name: messages.cancelDownload})).not.toBeInTheDocument();
    });
    it('updates stage progress and error details safely as React text', () => {
        const stage = makeStage({
            stage: 'Upload',
            status: 'Running',
            progress: null,
            errorCode: null,
            error: '<script>untrusted</script>'
        });
        const view = render(<HistoryDialog download={makeDownload({attempts: [makeAttempt({stages: [stage]})]})}
                                           error={false} onClose={vi.fn()}/>);
        expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow');
        view.rerender(<HistoryDialog
            download={makeDownload({attempts: [makeAttempt({stages: [{...stage, progress: 62}]})]})} error={false}
            onClose={vi.fn()}/>);
        expect(screen.getByRole('progressbar', {name: messages.stages.Upload})).toHaveAttribute('aria-valuenow', '62');
        expect(screen.getByText('<script>untrusted</script>')).toBeInTheDocument();
        expect(document.querySelector('script')).toBeNull();
        expect(within(screen.getByRole('dialog')).getAllByRole('button', {name: messages.close})).toHaveLength(2);
    });
});
