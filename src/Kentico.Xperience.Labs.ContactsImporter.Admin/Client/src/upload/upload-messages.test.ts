import { describe, expect, it } from 'vitest';

import { enMessage } from '../shared/test-resources';
import { workerErrorMessage } from '../worker/messages';
import { ImportWorkerError } from '../worker/protocol';

import { uploadFailureMessage } from './upload-messages';
import { UploadSessionError } from './upload-session';

const text = (failure: UploadSessionError) => enMessage(uploadFailureMessage(failure));

describe('uploadFailureMessage', () => {
  it('shows the server message, with the row when the server names one', () => {
    expect(text(new UploadSessionError('rejected', 'dev', { serverMessage: 'Not valid.', rowNumber: 17 }))).toBe(
      'The server did not accept the data: Not valid. (row 17)',
    );
    expect(text(new UploadSessionError('rejected', 'dev', { serverMessage: 'Not valid.' }))).toBe('The server did not accept the data: Not valid.');
    expect(text(new UploadSessionError('rejected', 'dev'))).toBe('The server did not accept the data.');
    expect(text(new UploadSessionError('complete', 'dev', { serverMessage: 'Missing parts.' }))).toBe('The upload could not be finished: Missing parts.');
    expect(text(new UploadSessionError('complete', 'dev'))).toBe('The upload could not be finished.');
  });

  it('has its own text for the size limit, network problems and files without importable rows', () => {
    expect(uploadFailureMessage(new UploadSessionError('rejected', 'dev', { reason: 'uploadTooLarge', serverMessage: 'x' })).key).toBe(
      'contactsimporter.upload.errors.toolarge',
    );
    expect(uploadFailureMessage(new UploadSessionError('network', 'dev')).key).toBe('contactsimporter.upload.errors.network');
    expect(uploadFailureMessage(new UploadSessionError('noRows', 'dev')).key).toBe('contactsimporter.upload.errors.norows');
  });

  it('never shows the developer text of the import core', () => {
    const invalidConfig = new ImportWorkerError('invalidConfig', 'Key field \'ContactEmail\' is not mapped.');

    expect(text(new UploadSessionError('worker', 'dev', { workerError: invalidConfig }))).toBe(
      'The mapping does not fit this file. Go back to the mapping and save it again.',
    );
  });
});

describe('workerErrorMessage', () => {
  it('describes known worker errors from their code and params', () => {
    expect(enMessage(workerErrorMessage(new ImportWorkerError('recordTooLarge', 'dev', { record: 1200, maxChars: 1048576 }), 'file'))).toBe(
      'Row 1200 of the file is longer than 1048576 characters. The file probably has an unbalanced quote or the wrong delimiter.',
    );
    expect(workerErrorMessage(new ImportWorkerError('recordTooLarge', 'dev', { line: 3, maxChars: 10 }), 'file')).toEqual({
      key: 'contactsimporter.file.linetoolarge',
      params: { row: 3, maxChars: 10 },
    });
    expect(enMessage(workerErrorMessage(new ImportWorkerError('unsupportedEncoding', 'dev', { encoding: 'x-foo' }), 'file'))).toBe(
      'The file cannot be read with the encoding x-foo. Choose another encoding.',
    );
    expect(workerErrorMessage(new ImportWorkerError('unsupportedFormat', 'dev'), 'upload').key).toBe('contactsimporter.file.unsupported');
  });

  it('shows the message of an unexpected error as the detail', () => {
    expect(enMessage(workerErrorMessage(new ImportWorkerError('failed', 'Out of memory'), 'file'))).toBe('The file could not be read: Out of memory');
    expect(enMessage(workerErrorMessage(new ImportWorkerError('failed', ''), 'upload'))).toBe('The file could not be processed.');
    expect(enMessage(workerErrorMessage(new TypeError('x is undefined'), 'upload'))).toBe('The file could not be processed: x is undefined');
  });
});
