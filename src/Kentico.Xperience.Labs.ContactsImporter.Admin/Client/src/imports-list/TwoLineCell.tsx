import { CellType } from '@kentico/xperience-admin-components';
import type { ComponentCell, TableCell } from '@kentico/xperience-admin-components';
import React from 'react';

interface TwoLineCellProps {
  readonly primary: string;
  readonly secondary: string;
  readonly secondaryTestId?: string;
}

// The admin table centres component cells; each line stays left-aligned under the first.
const LINE: React.CSSProperties = { maxWidth: '100%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' };

/**
 * Value with a second, smaller line (file and size, date and user), so the list needs fewer columns and fits a
 * 1440 px wide screen. Both lines end with an ellipsis when the column is too narrow; the title shows them in full.
 */
const TwoLineCell = ({ primary, secondary, secondaryTestId }: TwoLineCellProps) => (
  <div title={secondary ? `${primary}\n${secondary}` : primary} style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-start', textAlign: 'left', minWidth: 0, width: '100%', lineHeight: '18px' }}>
    <span style={LINE}>{primary}</span>
    {secondary && (
      <span data-testid={secondaryTestId} style={{ ...LINE, fontSize: '12px', color: 'var(--color-text-low-emphasis)' }}>
        {secondary}
      </span>
    )}
  </div>
);

/** Table cell with two lines; the admin `Table` renders `component` without props, so it closes over them. */
export function twoLineCell(columnName: string, primary: string, secondary: string, secondaryTestId?: string): TableCell {
  const component = () => <TwoLineCell primary={primary} secondary={secondary} secondaryTestId={secondaryTestId} />;
  component.displayName = 'ImportTwoLineCell';

  return { type: CellType.Component, columnName, component } as ComponentCell;
}
