import { describe, expect, it } from 'vitest';

import type { FieldDto, FieldSamplesDto, ValueSampleDto } from '../shared/commands';

import { SAMPLES_COLLAPSED_GROUPS, canCollapseSamples, sampleGroupCount, valueSamplesView, visibleSamples } from './value-samples';

function field(name: string, caption: string): FieldDto {
  return { name, caption, dataType: 'text', maxLength: null, isSupported: true, canBeKey: false };
}

function sample(row: number, current: string | null, value: string | null): ValueSampleDto {
  return { row, key: `user${row}@example.com`, current, new: value };
}

const FIELDS: FieldDto[] = [field('ContactFirstName', 'First name'), field('ContactLastName', 'ContactLastName'), field('ContactCity', 'City')];

describe('valueSamplesView', () => {
  it('puts cleared values first and orders fields by their readable name', () => {
    const samples: FieldSamplesDto[] = [
      { field: 'ContactLastName', cleared: [sample(2, 'Adams', null)], changed: [sample(1, 'Black', 'Brown')] },
      { field: 'ContactFirstName', cleared: [], changed: [sample(3, 'Anna', 'Ann'), sample(5, null, 'Bob')] },
      { field: 'CustomField', cleared: [sample(4, 'x', null)], changed: [] },
    ];

    const view = valueSamplesView(samples, FIELDS);

    expect(view.cleared.map((group) => [group.field, group.label, group.cleared])).toEqual([
      ['CustomField', 'CustomField', true],
      ['ContactLastName', 'Last name', true],
    ]);
    expect(view.changed.map((group) => group.label)).toEqual(['First name', 'Last name']);
    expect(view.changed[0].samples.map((s) => s.row)).toEqual([3, 5]);
    expect(sampleGroupCount(view)).toBe(4);
  });

  it('is empty when the check found no changes', () => {
    const view = valueSamplesView([], FIELDS);

    expect(view).toEqual({ cleared: [], changed: [] });
    expect(canCollapseSamples(view)).toBe(false);
  });
});

describe('visibleSamples', () => {
  const many: FieldSamplesDto[] = Array.from({ length: 6 }, (_, i) => ({
    field: `Field${i}`,
    cleared: i < 2 ? [sample(i, 'a', null)] : [],
    changed: [sample(i + 10, 'a', 'b')],
  }));

  it('shows the first groups, cleared ones first, until expanded', () => {
    const view = valueSamplesView(many, FIELDS);
    const collapsed = visibleSamples(view, false);

    expect(canCollapseSamples(view)).toBe(true);
    expect(sampleGroupCount(collapsed)).toBe(SAMPLES_COLLAPSED_GROUPS);
    expect(collapsed.cleared.map((group) => group.field)).toEqual(['Field0', 'Field1']);
    expect(collapsed.changed.map((group) => group.field)).toEqual(['Field0', 'Field1']);
    expect(visibleSamples(view, true)).toBe(view);
  });

  it('never hides cleared groups behind changed ones', () => {
    const allCleared: FieldSamplesDto[] = Array.from({ length: 6 }, (_, i) => ({ field: `Field${i}`, cleared: [sample(i, 'a', null)], changed: [sample(i, 'a', 'b')] }));
    const collapsed = visibleSamples(valueSamplesView(allCleared, FIELDS), false);

    expect(collapsed.cleared).toHaveLength(SAMPLES_COLLAPSED_GROUPS);
    expect(collapsed.changed).toHaveLength(0);
  });

  it('shows short lists whole', () => {
    const view = valueSamplesView(many.slice(0, 2), FIELDS);

    expect(sampleGroupCount(view)).toBe(SAMPLES_COLLAPSED_GROUPS);
    expect(visibleSamples(view, false)).toBe(view);
  });
});
