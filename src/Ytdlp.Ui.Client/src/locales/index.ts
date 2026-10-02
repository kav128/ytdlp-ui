import en from './en/common.json';
import ru from './ru/common.json';

export function selectLanguage(preferences: readonly string[]): 'en' | 'ru' {
  for (const preference of preferences) {
    const language = preference.toLowerCase().split('-')[0];
    if (language === 'en' || language === 'ru') return language;
  }
  return 'en';
}

export const language = selectLanguage(navigator.languages);
export const messages = { en, ru }[language];
