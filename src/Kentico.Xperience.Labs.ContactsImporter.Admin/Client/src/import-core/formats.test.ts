import { describe, expect, it } from 'vitest';

import type { EncodedChunk } from './chunker';
import type { TargetField } from './contract';
import { buildZip, tableXlsx } from './format-test-helpers';
import { openRecordSource } from './open';
import { createImportPipeline } from './pipeline';
import type { ImportPipelineOptions } from './pipeline';
import { makeFile, randomSource, randomWord } from './test-helpers';

const FIELDS: TargetField[] = [
  { name: 'ContactEmail', caption: 'Email', dataType: 'text', maxLength: 254 },
  { name: 'ContactFirstName', caption: 'First name', dataType: 'text', maxLength: 100 },
  { name: 'ContactCity', caption: 'City', dataType: 'text', maxLength: 100 },
];

const OPTIONS: ImportPipelineOptions = {
  jobId: '3f0e8a0e-5b7b-4f0a-9d5e-0a3b7f0c1d2e',
  keyField: 'ContactEmail',
  mapping: [
    { source: 'Email', target: 'ContactEmail' },
    { source: 'First Name', target: 'ContactFirstName' },
    { source: 'City', target: 'ContactCity' },
  ],
  fields: FIELDS,
  targetChunkBytes: 8 * 1024,
};

/** Header + rows with some empty values, invalid emails and duplicate keys. */
function table(rows: number): string[][] {
  const random = randomSource(11);
  const result = [['Email', 'First Name', 'City', 'Notes']];
  for (let i = 0; i < rows; i++) {
    const email = i % 97 === 0 ? 'not-an-email' : i % 50 === 0 ? 'dup@example.com' : `${randomWord(random, 8)}${i}@example.com`;
    result.push([email, i % 7 === 0 ? '' : `Ann ${randomWord(random, 5)}`, i % 5 === 0 ? '' : 'Brno', `note ${i}`]);
  }

  return result;
}

function toCsv(rows: string[][]): string {
  return rows.map((row) => row.join(',')).join('\n');
}

function toJson(rows: string[][]): string {
  const [header, ...data] = rows;

  return JSON.stringify(data.map((row) => Object.fromEntries(header.map((name, i) => [name, row[i]]))), null, 1);
}

async function run(bytes: string | Uint8Array, name: string) {
  const source = await openRecordSource(makeFile(bytes, name, 1000));
  const pipeline = createImportPipeline(source, OPTIONS);
  const chunks: EncodedChunk[] = [];
  for await (const chunk of pipeline.chunks()) {
    chunks.push(chunk);
  }
  const { profile, rejectedRows, suspectRows } = pipeline.result();

  return { chunks: chunks.map((chunk) => chunk.payload), profile, rejectedRows, suspectRows };
}

describe('import pipeline: formats', () => {
  it('produces the same chunks from CSV, JSON array, XLSX and zipped files of the same table', async () => {
    const rows = table(3000);
    const csv = toCsv(rows);
    const expected = await run(csv, 'contacts.csv');
    expect(expected.chunks.length).toBeGreaterThan(2);
    expect(expected.rejectedRows.length).toBeGreaterThan(0);
    expect(expected.suspectRows.length).toBeGreaterThan(0);

    const json = toJson(rows);
    const sources: [string, string | Uint8Array][] = [
      ['contacts.json', json],
      ['contacts.xlsx', await tableXlsx(rows, true)],
      ['inline.xlsx', await tableXlsx(rows, false)],
      ['csv.zip', await buildZip([{ name: 'contacts.csv', data: csv, method: 8 }])],
      ['json.zip', await buildZip([{ name: 'contacts.json', data: json, method: 8 }])],
      ['xlsx.zip', await buildZip([{ name: 'contacts.xlsx', data: await tableXlsx(rows) }])],
    ];
    for (const [name, bytes] of sources) {
      expect(await run(bytes, name), name).toEqual(expected);
    }
  });
});
