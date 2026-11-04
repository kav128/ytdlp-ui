import {describe, expect, it} from 'vitest';
import {selectLanguage} from './index';
import en from './en/common.json';
import ru from './ru/common.json';

describe('language resources', () => {
    it.each([
        [['ru-RU', 'en-US'], 'ru'],
        [['en-GB', 'ru'], 'en'],
        [['de-DE', 'RU'], 'ru'],
        [['fr-FR'], 'en'],
        [[], 'en'],
    ] as const)('chooses a supported browser language from %j', (preferences, expected) => {
        expect(selectLanguage(preferences)).toBe(expected);
    });

    it('provides all base resource keys in Russian', () => {
        expect(Object.keys(ru).sort()).toEqual(Object.keys(en).sort());
    });
    it('provides nested stage, history, continuation and error labels in both languages', () => {
        for (const name of ['states', 'stages', 'executionStatuses', 'attemptReasons', 'publicationStates', 'explanations', 'errors'] as const) {
            expect(Object.keys(ru[name]).sort()).toEqual(Object.keys(en[name]).sort());
            for (const text of [...Object.values(en[name]), ...Object.values(ru[name])]) expect(text.trim().length).toBeGreaterThan(0);
        }
        for (const code of ['artifacts_unavailable', 'media_plan_missing', 'publication_reconciliation'] as const) {
            expect(en.explanations[code]).not.toBe(code);
            expect(ru.explanations[code]).not.toBe(en.explanations[code]);
        }
    });
});
