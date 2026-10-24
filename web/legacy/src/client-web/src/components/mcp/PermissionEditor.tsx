import { useEffect, useMemo, useRef } from 'react';
import type { McpPermissions, McpToolInfo } from '../../types';

interface PermissionEditorProps {
  readTools: McpToolInfo[];
  writeTools: McpToolInfo[];
  permissions: McpPermissions;
  onChange: (permissions: McpPermissions) => void;
}

function groupBySection(tools: McpToolInfo[]): Map<string, McpToolInfo[]> {
  const map = new Map<string, McpToolInfo[]>();
  for (const tool of tools) {
    const list = map.get(tool.group) ?? [];
    list.push(tool);
    map.set(tool.group, list);
  }
  return map;
}

function GroupCheckbox({
  tools,
  checkedNames,
  onChange,
  section,
}: {
  tools: McpToolInfo[];
  checkedNames: Set<string>;
  onChange: (section: 'read' | 'write', names: string[], enabled: boolean) => void;
  section: 'read' | 'write';
}) {
  const ref = useRef<HTMLInputElement>(null);
  const all = tools.length > 0 && tools.every(t => checkedNames.has(t.name));
  const partial = !all && tools.some(t => checkedNames.has(t.name));

  useEffect(() => {
    if (ref.current) ref.current.indeterminate = partial;
  }, [partial, all]);

  return (
    <label className="flex items-center gap-2 text-sm font-semibold text-slate-700">
      <input
        ref={ref}
        type="checkbox"
        checked={all}
        onChange={event => onChange(section, tools.map(t => t.name), event.target.checked)}
        className="rounded border-slate-300 text-blue-600 focus:ring-blue-500"
      />
      {tools[0]?.group ?? '未分组'}
      <span className="text-xs font-normal text-slate-400">({tools.length})</span>
    </label>
  );
}

export default function PermissionEditor({ readTools, writeTools, permissions, onChange }: PermissionEditorProps) {
  const readGroups = useMemo(() => groupBySection(readTools), [readTools]);
  const writeGroups = useMemo(() => groupBySection(writeTools), [writeTools]);

  const readEnabled = new Set(
    Object.entries(permissions.read ?? {}).filter(([, v]) => v).map(([k]) => k)
  );
  const writeEnabled = new Set(
    Object.entries(permissions.write ?? {}).filter(([, v]) => v).map(([k]) => k)
  );

  function toggleOne(section: 'read' | 'write', name: string, enabled: boolean) {
    const next = { ...permissions, [section]: { ...permissions[section], [name]: enabled } };
    onChange(next);
  }

  function toggleGroup(section: 'read' | 'write', names: string[], enabled: boolean) {
    const map = { ...permissions[section] };
    for (const name of names) map[name] = enabled;
    onChange({ ...permissions, [section]: map });
  }

  function setAll(section: 'read' | 'write', enabled: boolean) {
    const tools = section === 'read' ? readTools : writeTools;
    const map: Record<string, boolean> = {};
    for (const tool of tools) map[tool.name] = enabled;
    onChange({ ...permissions, [section]: map });
  }

  function renderSection(
    section: 'read' | 'write',
    groups: Map<string, McpToolInfo[]>,
    enabled: Set<string>
  ) {
    const total = [...groups.values()].reduce((sum, g) => sum + g.length, 0);
    const onCount = [...enabled].length;
    return (
      <div className="rounded-lg border border-slate-200 bg-white p-4">
        <div className="mb-3 flex items-center justify-between">
          <h3 className="text-sm font-bold text-slate-800">
            {section === 'read' ? '读取权限' : '写入权限'}
            <span className="ml-2 text-xs font-normal text-slate-400">
              {onCount}/{total} 已开启
            </span>
          </h3>
          <div className="flex gap-2 text-xs">
            <button
              type="button"
              onClick={() => setAll(section, true)}
              className="rounded border border-slate-200 px-2 py-1 font-medium text-slate-600 hover:bg-slate-50"
            >
              全开
            </button>
            <button
              type="button"
              onClick={() => setAll(section, false)}
              className="rounded border border-slate-200 px-2 py-1 font-medium text-slate-600 hover:bg-slate-50"
            >
              全关
            </button>
          </div>
        </div>
        <div className="space-y-2">
          {[...groups.entries()].map(([groupName, tools]) => (
            <details key={groupName} className="group rounded-md border border-slate-100 p-2">
              <summary className="flex cursor-pointer list-none items-center justify-between">
                <GroupCheckbox
                  tools={tools}
                  checkedNames={enabled}
                  onChange={toggleGroup}
                  section={section}
                />
                <span className="text-xs text-slate-400 group-open:hidden">展开</span>
                <span className="hidden text-xs text-slate-400 group-open:inline">收起</span>
              </summary>
              <div className="mt-2 space-y-1.5 pl-6">
                {tools.map(tool => (
                  <label key={tool.name} className="flex items-center gap-2 text-sm text-slate-600">
                    <input
                      type="checkbox"
                      checked={enabled.has(tool.name)}
                      onChange={event => toggleOne(section, tool.name, event.target.checked)}
                      className="rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                    />
                    <span className="truncate font-mono text-xs" title={tool.description}>
                      {tool.name}
                    </span>
                  </label>
                ))}
              </div>
            </details>
          ))}
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {renderSection('read', readGroups, readEnabled)}
      {renderSection('write', writeGroups, writeEnabled)}
    </div>
  );
}
