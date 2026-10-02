import { language, messages } from './index';

export function formatDate(value: string) {
  return new Intl.DateTimeFormat(language, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value));
}

export function formatSize(bytes: number) {
  const units = messages.sizeUnits;
  const index = Math.min(bytes > 0 ? Math.floor(Math.log(bytes) / Math.log(1024)) : 0, units.length - 1);
  return `${new Intl.NumberFormat(language, { maximumFractionDigits: index === 0 ? 0 : 1 }).format(bytes / 1024 ** index)} ${units[index]}`;
}

export function formatNumber(value: number) { return new Intl.NumberFormat(language).format(value); }
export function stateLabel(state: string): string { return (messages.states as Record<string, string>)[state] ?? messages.states.unknown; }
export function errorLabel(code: string): string { return (messages.errors as Record<string, string>)[code] ?? messages.errors.request_failed; }
