import { CellType, Icon } from '@kentico/xperience-admin-components';
import type { ComponentCell, TableCell } from '@kentico/xperience-admin-components';
import React from 'react';

import type { JobState } from '../shared/commands';
import { stateIcon } from './stateIcon';

interface StateCellProps {
  readonly state: JobState | string;
  readonly label: string;
}

/** Job state with its colored admin icon. The icon is decorative, so the cell's text is the state label alone. */
const StateCell = ({ state, label }: StateCellProps) => {
  const { name, color } = stateIcon(state);

  return (
    <div title={label} style={{ display: 'flex', alignItems: 'center', gap: '0.5em', minWidth: 0 }}>
      <span aria-hidden="true" style={{ display: 'inline-flex', flexShrink: 0, color }}>
        <Icon name={name} />
      </span>
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label}</span>
    </div>
  );
};

/** Table cell for the state column; the admin `Table` renders `component` without props, so it closes over them. */
export function stateCell(columnName: string, state: JobState | string, label: string): TableCell {
  const component = () => <StateCell state={state} label={label} />;
  component.displayName = 'ImportStateCell';

  return { type: CellType.Component, columnName, component } as ComponentCell;
}
