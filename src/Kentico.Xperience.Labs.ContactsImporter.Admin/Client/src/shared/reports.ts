import type { RejectedRow } from '../import-core';

import { Strings } from './strings';

/**
 * Client-side reports of the Upload step. They are built from row numbers and reason codes
 * only (no values of the file) and never leave the browser.
 */

/** CSV of the rows skipped in the browser: row number, reason, field, parser detail. */
export function rejectedRowsCsv(rows: readonly RejectedRow[]): string {
  const { csv, upload } = Strings;
  const lines = [`${csv.row},${csv.reason},${csv.field},${csv.detail}`];
  for (const row of rows) {
    lines.push(
      [String(row.rowNumber), csvValue(upload.reasons[row.reason] ?? row.reason), csvValue(row.field ?? ''), csvValue(row.detail ?? '')].join(','),
    );
  }

  return `${lines.join('\r\n')}\r\n`;
}

/** CSV of the row numbers whose match value probably appeared earlier in the file. */
export function suspectRowsCsv(rowNumbers: readonly number[]): string {
  return `${[Strings.csv.row, ...rowNumbers.map(String)].join('\r\n')}\r\n`;
}

/** `contacts.csv` + `skipped-rows` -> `contacts-skipped-rows.csv`. */
export function reportFileName(sourceFileName: string, suffix: string): string {
  const base = sourceFileName.replace(/(\.gz)$/i, '').replace(/\.[^.]+$/, '').replace(/[^\p{L}\p{N}._-]+/gu, '-');

  return `${base || 'import'}-${suffix}.csv`;
}

/** Quotes a CSV value; a leading `= + - @` is neutralized so a spreadsheet never runs it as a formula. */
export function csvValue(value: string): string {
  const safe = /^[=+\-@\t\r]/.test(value) ? `'${value}` : value;

  return /[",\r\n]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe;
}

/** Saves text as a file from the browser (no server round trip). */
export function downloadTextFile(fileName: string, content: string, mimeType = 'text/csv;charset=utf-8'): void {
  // The BOM makes Excel read the file as UTF-8.
  const url = URL.createObjectURL(new Blob(['﻿', content], { type: mimeType }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.style.display = 'none';
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
