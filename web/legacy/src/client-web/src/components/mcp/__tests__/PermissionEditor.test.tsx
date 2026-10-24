import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import PermissionEditor from '../PermissionEditor';
import type { McpPermissions, McpToolInfo } from '../../../types';

const readTools: McpToolInfo[] = [
  { name: 'get_events', group: 'calendar', description: 'r', isWrite: false },
  { name: 'get_tasks', group: 'calendar', description: 'r', isWrite: false },
  { name: 'get_notes', group: 'quicknotes', description: 'r', isWrite: false },
];
const writeTools: McpToolInfo[] = [
  { name: 'create_task', group: 'calendar.tasks', description: 'w', isWrite: true },
  { name: 'create_note', group: 'quicknotes', description: 'w', isWrite: true },
];

const basePermissions: McpPermissions = {
  read: { get_events: true, get_tasks: false, get_notes: true },
  write: { create_task: false, create_note: true },
};

describe('PermissionEditor', () => {
  it('renders both sections with counts', () => {
    render(
      <PermissionEditor
        readTools={readTools}
        writeTools={writeTools}
        permissions={basePermissions}
        onChange={() => {}}
      />
    );
    expect(screen.getByText('读取权限')).toBeTruthy();
    expect(screen.getByText('写入权限')).toBeTruthy();
    expect(screen.getByText(/2\/3 已开启/)).toBeTruthy();
    expect(screen.getByText(/1\/2 已开启/)).toBeTruthy();
  });

  it('toggle single tool calls onChange', () => {
    const onChange = vi.fn();
    render(
      <PermissionEditor
        readTools={readTools}
        writeTools={writeTools}
        permissions={basePermissions}
        onChange={onChange}
      />
    );
    fireEvent.click(screen.getByLabelText(/get_tasks/));
    expect(onChange).toHaveBeenCalled();
    const next = onChange.mock.calls[0][0] as McpPermissions;
    expect(next.read.get_tasks).toBe(true);
  });

  it('全关 clears the write section', () => {
    const onChange = vi.fn();
    render(
      <PermissionEditor
        readTools={readTools}
        writeTools={writeTools}
        permissions={basePermissions}
        onChange={onChange}
      />
    );
    const buttons = screen.getAllByText('全关');
    fireEvent.click(buttons[1]); // write section 全关
    const next = onChange.mock.calls[0][0] as McpPermissions;
    expect(next.write.create_task).toBe(false);
    expect(next.write.create_note).toBe(false);
  });
});
