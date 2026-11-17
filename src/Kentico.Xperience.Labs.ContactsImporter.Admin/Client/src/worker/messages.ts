import type { Message } from '../shared/text';

import { ImportWorkerError } from './protocol';

/**
 * Text of a worker error for the File step (`file`) or the Upload step (`upload`). Known codes get
 * their own text from the code and its params; an unexpected error (`failed`) shows its message
 * as the detail, because it is all there is to say about it.
 */
export function workerErrorMessage(error: unknown, step: 'file' | 'upload'): Message {
  const failed = (detail: string): Message =>
    step === 'file'
      ? detail
        ? { key: 'contactsimporter.file.readfailed', params: { reason: detail } }
        : { key: 'contactsimporter.file.readfailednodetail' }
      : detail
        ? { key: 'contactsimporter.upload.errors.worker', params: { reason: detail } }
        : { key: 'contactsimporter.upload.errors.workernodetail' };

  if (!(error instanceof ImportWorkerError)) {
    return failed(error instanceof Error ? error.message : '');
  }

  const { params } = error;
  switch (error.code) {
    case 'unsupportedFormat':
      return { key: 'contactsimporter.file.unsupported' };
    case 'unsupportedEncoding':
      return { key: 'contactsimporter.file.unsupportedencoding', params: { encoding: String(params.encoding ?? '') } };
    case 'recordTooLarge':
      return params.line !== undefined
        ? { key: 'contactsimporter.file.linetoolarge', params: { row: params.line, maxChars: params.maxChars ?? 0 } }
        : { key: 'contactsimporter.file.recordtoolarge', params: { row: params.record ?? 0, maxChars: params.maxChars ?? 0 } };
    case 'invalidConfig':
    case 'sourceConsumed':
      // The mapping or the reader settings do not fit the file; the message names developer details.
      return step === 'upload' ? { key: 'contactsimporter.upload.errors.invalidconfig' } : failed('');
    default:
      return failed(error.message);
  }
}
