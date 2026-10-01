const paths = {
  library: 'M4 6h16v14H4z M8 3h8 M7 10h10 M7 14h6',
  download: 'M12 3v12 m-5-5 5 5 5-5 M4 17v4h16v-4',
  plus: 'M12 5v14 M5 12h14',
  login: 'M14 4h6v16h-6 M3 12h12 m-5-5 5 5-5 5',
  logout: 'M10 4H4v16h6 M10 12h11 m-5-5 5 5-5 5',
  file: 'M6 3h8l4 4v14H6z M14 3v5h4 M9 12h6 M9 16h6',
  trash: 'M4 6h16 M9 3h6 M6 6l1 15h10l1-15 M10 10v7 M14 10v7',
  retry: 'M4 10a8 8 0 1 1 1 8 M4 4v6h6',
  close: 'M6 6l12 12 M18 6 6 18',
  clock: 'M12 8v5l3 2 M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0',
  info: 'M12 11v6 M12 7h.01 M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0',
  check: 'M5 12l4 4L19 6',
  arrow: 'M5 12h14 m-6-6 6 6-6 6',
} as const;

export default function Icon({ name, className = '' }: { name: keyof typeof paths; className?: string }) {
  return <svg className={`icon ${className}`} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[name]} /></svg>;
}
