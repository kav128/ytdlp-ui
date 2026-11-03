import {act, fireEvent, render, screen} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import {api, type LibraryFile} from '../../api/client';
import {messages} from '../../locales';
import type {AuthenticatedRequest} from '../auth/useAuth';
import UploadDialog from './UploadDialog';

afterEach(() => vi.restoreAllMocks());

it('uses a localized accessible chooser and filename while keeping native UI hidden and disabling selection during upload', async () => {
    let complete!: (file: LibraryFile) => void;
    vi.spyOn(api, 'upload').mockReturnValue(new Promise(resolve => {
        complete = resolve;
    }));
    const authorized: AuthenticatedRequest = operation => operation('csrf');
    const uploaded = vi.fn();
    render(<UploadDialog sessionId="session" authorized={authorized} unauthorized={vi.fn()} onClose={vi.fn()}
                         onUploaded={uploaded}/>);
    const input = screen.getByLabelText(messages.chooseFile) as HTMLInputElement;
    const chooser = screen.getByRole('button', {name: messages.selectFile});
    expect(input).not.toBeVisible();
    expect(chooser).toHaveAccessibleDescription(messages.noFileSelected);
    const open = vi.spyOn(input, 'click').mockImplementation(() => {
    });
    chooser.focus();
    expect(chooser).toHaveFocus();
    fireEvent.click(chooser);
    expect(open).toHaveBeenCalledTimes(1);
    const file = new File([], 'произвольный +%#.bin');
    fireEvent.change(input, {target: {files: [file]}});
    expect(screen.getByText(file.name)).toBeVisible();
    expect(chooser).toHaveAccessibleDescription(file.name);
    fireEvent.submit(input.closest('form')!);
    await act(async () => {
    });
    expect(chooser).toBeDisabled();
    expect(input).toBeDisabled();
    expect(api.upload).toHaveBeenCalledWith(file, 'csrf', expect.any(AbortSignal));
    await act(async () => complete({id: 'published', name: file.name, size: 0, lastModified: '', contentUrl: ''}));
    expect(uploaded).toHaveBeenCalledTimes(1);
});
