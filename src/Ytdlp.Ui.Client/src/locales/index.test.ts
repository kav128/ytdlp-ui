import { describe, expect, it } from 'vitest';
import { selectLanguage } from './index';
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
});
