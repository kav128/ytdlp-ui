import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { language, messages } from './locales';
import './styles/app.css';

document.documentElement.lang = language;
document.title = messages.appTitle;

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
