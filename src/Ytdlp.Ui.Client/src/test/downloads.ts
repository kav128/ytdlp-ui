import type {Attempt, Download, Stage} from '../api/client';

export function makeStage(overrides: Partial<Stage> = {}): Stage {
    return {
        id: 'stage-cleanup', stage: 'Cleanup', status: 'Failed', startedAt: '2026-10-01T10:02:00Z',
        finishedAt: '2026-10-01T10:03:00Z', progress: null, errorCode: 'cleanup_failed', error: null,
        artifactId: null, reusedFromStageId: null, ...overrides
    };
}

export function makeAttempt(overrides: Partial<Attempt> = {}): Attempt {
    return {
        id: 'attempt-one',
        number: 1,
        reason: 'Initial',
        status: 'Failed',
        initialState: 'Downloading',
        state: 'Failed',
        createdAt: '2026-10-01T10:00:00Z',
        startedAt: '2026-10-01T10:01:00Z',
        finishedAt: '2026-10-01T10:03:00Z',
        errorCode: 'cleanup_failed',
        continuationReasonCode: null,
        error: 'Demo error',
        stages: [makeStage()], ...overrides
    };
}

export function makeDownload(overrides: Partial<Download> = {}): Download {
    return {
        id: 'private-job',
        title: 'Private video',
        url: 'https://example.com/video',
        state: 'Failed',
        resumeState: 'CleaningUp',
        progress: null,
        createdAt: '2026-10-01T10:00:00Z',
        error: 'Demo error',
        errorCode: 'cleanup_failed',
        currentAttemptId: 'attempt-one',
        publicationId: 'publication-one',
        publicationState: 'Available',
        failedState: 'CleaningUp',
        cancelRequested: false,
        version: 1,
        allowedActions: ['retry'],
        attempts: [makeAttempt()], ...overrides
    };
}
