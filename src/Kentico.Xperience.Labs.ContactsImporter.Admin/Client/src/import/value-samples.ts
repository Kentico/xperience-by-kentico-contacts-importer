import type {
  FieldDto,
  FieldSamplesDto,
  ValueSampleDto,
} from "../shared/commands";

import { fieldLabel } from "./model";

/** Examples of one contact field and kind (cleared or changed), lowest rows first. */
export interface SampleGroup {
  /** Contact field (column) name. */
  readonly field: string;
  /** Readable field name. */
  readonly label: string;
  readonly cleared: boolean;
  readonly samples: readonly ValueSampleDto[];
}

export interface ValueSamplesView {
  /** Fields with values the import clears; shown first. */
  readonly cleared: readonly SampleGroup[];
  /** Fields with values the import replaces. */
  readonly changed: readonly SampleGroup[];
}

/** Groups (cleared and changed together) shown before "Show all". */
export const SAMPLES_COLLAPSED_GROUPS = 4;

/**
 * The examples of `GET_SAMPLES` for the Review step: cleared values first, then changed values; per kind
 * the fields in the order of their readable names. Fields without examples of a kind are left out.
 */
export function valueSamplesView(
  samples: readonly FieldSamplesDto[],
  fields: readonly FieldDto[],
): ValueSamplesView {
  const labelOf = (name: string) => {
    const field = fields.find((candidate) => candidate.name === name);

    return field ? fieldLabel(field) : name;
  };
  const groups = (cleared: boolean): SampleGroup[] =>
    samples
      .map((entry) => ({
        field: entry.field,
        label: labelOf(entry.field),
        cleared,
        samples: cleared ? entry.cleared : entry.changed,
      }))
      .filter((group) => group.samples.length > 0)
      .sort(
        (a, b) =>
          a.label.localeCompare(b.label) || a.field.localeCompare(b.field),
      );

  return { cleared: groups(true), changed: groups(false) };
}

/** Number of field groups of both kinds. */
export function sampleGroupCount(view: ValueSamplesView): number {
  return view.cleared.length + view.changed.length;
}

/** Whether the view is long enough for "Show all". */
export function canCollapseSamples(view: ValueSamplesView): boolean {
  return sampleGroupCount(view) > SAMPLES_COLLAPSED_GROUPS;
}

/**
 * Groups to show: all when expanded or few, otherwise the first `SAMPLES_COLLAPSED_GROUPS`, cleared
 * groups first (so cleared values are never hidden behind changed ones).
 */
export function visibleSamples(
  view: ValueSamplesView,
  expanded: boolean,
): ValueSamplesView {
  if (expanded || !canCollapseSamples(view)) {
    return view;
  }

  const cleared = view.cleared.slice(0, SAMPLES_COLLAPSED_GROUPS);

  return {
    cleared,
    changed: view.changed.slice(0, SAMPLES_COLLAPSED_GROUPS - cleared.length),
  };
}
