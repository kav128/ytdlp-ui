import {afterEach, describe, expect, it, vi} from 'vitest';
import {api} from './client';

afterEach(() => vi.unstubAllGlobals());
describe('cookie API transport', () => {
    it('sends multipart FormData and lets the browser set its boundary', async () => {
        const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({id: 'file'}), {status: 201}));
        vi.stubGlobal('fetch', fetcher);
        const file = new File(['arbitrary bytes'], 'данные.zip', {type: 'application/zip'});
        await api.upload(file, 'csrf-token');
        const [url, options] = fetcher.mock.calls[0];
        expect(url).toBe('/api/library/uploads');
        expect(options.body).toBeInstanceOf(FormData);
        expect(options.body.get('file')).toBe(file);
        expect(options.headers.has('Content-Type')).toBe(false);
        expect(options.headers.get('X-CSRF-TOKEN')).toBe('csrf-token');
    });
    it('sends same-origin credentials and CSRF without Authorization', async () => {
        const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({id: 'download'}), {status: 201}));
        vi.stubGlobal('fetch', fetcher);
        await api.create('https://example.com/video', 'csrf-token');
        const [url, options] = fetcher.mock.calls[0];
        expect(url).toBe('/api/downloads');
        expect(options.credentials).toBe('same-origin');
        expect(options.headers.get('X-CSRF-TOKEN')).toBe('csrf-token');
        expect(options.headers.has('Authorization')).toBe(false);
    });
});
