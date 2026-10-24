import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import PageHeader from '../ui/PageHeader';
import EditorDrawer from '../ui/EditorDrawer';
import StatusBadge from '../ui/StatusBadge';
import PermissionEditor from '../components/mcp/PermissionEditor';
import {
  createMcpClient,
  deleteMcpClient,
  getMcpActivity,
  getMcpCatalog,
  listMcpClients,
  revokeMcpClient,
  updateMcpClient,
} from '../api/mcp';
import type { McpActivityLogEntry, McpClient, McpPermissions } from '../types';

function relativeTime(iso: string | null): string {
  if (!iso) return '从未';
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return `${Math.floor(seconds)} 秒前`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分钟前`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} 小时前`;
  return `${Math.floor(seconds / 86400)} 天前`;
}

const TOKEN_CONFIG_EXAMPLE = `{
  "mcpServers": {
    "pim": {
      "type": "http",
      "url": "https://<host>:<port>/mcp",
      "headers": { "Authorization": "Bearer <PIM_MCP_TOKEN>" }
    }
  }
}`;

export default function McpSettingsPage() {
  const qc = useQueryClient();
  const { data: clients = [], isLoading: loadingClients } = useQuery({
    queryKey: ['mcp-clients'],
    queryFn: listMcpClients,
    refetchInterval: 10_000,
  });
  const { data: catalog } = useQuery({ queryKey: ['mcp-catalog'], queryFn: getMcpCatalog });
  const { data: activityLogs = [] } = useQuery({
    queryKey: ['mcp-activity'],
    queryFn: getMcpActivity,
    refetchInterval: 10_000,
  });

  const [createOpen, setCreateOpen] = useState(false);
  const [createName, setCreateName] = useState('');
  const [createdToken, setCreatedToken] = useState<string | null>(null);
  const [editing, setEditing] = useState<McpClient | null>(null);
  const [draftPermissions, setDraftPermissions] = useState<McpPermissions | null>(null);

  const invalidate = () => qc.invalidateQueries({ queryKey: ['mcp-clients'] });

  const createMutation = useMutation({
    mutationFn: (name: string) => createMcpClient(name),
    onSuccess: result => {
      setCreatedToken(result.token);
      invalidate();
    },
  });

  const savePermissionsMutation = useMutation({
    mutationFn: ({ id, permissions }: { id: string; permissions: McpPermissions }) =>
      updateMcpClient(id, { permissions }),
    onSuccess: () => {
      invalidate();
      setEditing(null);
      setDraftPermissions(null);
    },
  });

  const revokeMutation = useMutation({
    mutationFn: (id: string) => revokeMcpClient(id),
    onSuccess: invalidate,
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => deleteMcpClient(id),
    onSuccess: invalidate,
  });

  const readTools = useMemo(() => catalog?.read ?? [], [catalog]);
  const writeTools = useMemo(() => catalog?.write ?? [], [catalog]);

  async function copyToken(token: string) {
    try {
      await navigator.clipboard.writeText(token);
    } catch {
      /* 剪贴板不可用时忽略 */
    }
  }

  function openEditor(client: McpClient) {
    setEditing(client);
    setDraftPermissions(client.permissions);
  }

  function closeCreate() {
    setCreateOpen(false);
    setCreateName('');
    setCreatedToken(null);
    createMutation.reset();
  }

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6 pb-20">
      <PageHeader
        title="MCP 连接"
        subtitle="管理 AI Agent 客户端连接、Token 与工具级权限"
        actions={
          <button type="button" onClick={() => setCreateOpen(true)} className="pim-button-primary px-4 py-2 text-sm">
            新建客户端
          </button>
        }
      />

      <section className="pim-panel p-4">
        <h2 className="mb-3 text-sm font-bold text-slate-800">客户端列表</h2>
        {loadingClients ? (
          <p className="rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
            正在加载...
          </p>
        ) : clients.length === 0 ? (
          <p className="rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
            还没有客户端。点击「新建客户端」生成第一个 Token。
          </p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-slate-200 text-xs font-medium text-slate-500">
                <tr>
                  <th className="px-2 py-2">客户端</th>
                  <th className="px-2 py-2">状态</th>
                  <th className="px-2 py-2">最后活跃</th>
                  <th className="px-2 py-2">调用次数</th>
                  <th className="px-2 py-2">最近工具</th>
                  <th className="px-2 py-2">操作</th>
                </tr>
              </thead>
              <tbody>
                {clients.map(client => (
                  <tr key={client.id} className="border-b border-slate-100 hover:bg-slate-50">
                    <td className="px-2 py-2.5">
                      <div className="font-semibold text-slate-900">{client.name}</div>
                      <div className="text-xs text-slate-400 font-mono">{client.tokenPrefix}…</div>
                    </td>
                    <td className="px-2 py-2.5">
                      {client.status === 'revoked' ? (
                        <StatusBadge tone="danger">已吊销</StatusBadge>
                      ) : client.online ? (
                        <StatusBadge tone="activity">在线</StatusBadge>
                      ) : (
                        <StatusBadge tone="neutral">离线</StatusBadge>
                      )}
                    </td>
                    <td className="px-2 py-2.5 text-slate-600">{relativeTime(client.lastSeenAt)}</td>
                    <td className="px-2 py-2.5 text-slate-600">
                      {client.callCount}
                      {client.writeCallCount > 0 && (
                        <span className="ml-1 text-xs text-amber-600">(写 {client.writeCallCount})</span>
                      )}
                    </td>
                    <td className="px-2 py-2.5 font-mono text-xs text-slate-500">{client.lastTool ?? '—'}</td>
                    <td className="px-2 py-2.5">
                      <div className="flex gap-3 text-xs font-medium">
                        <button
                          type="button"
                          className="text-blue-600 hover:text-blue-800"
                          onClick={() => openEditor(client)}
                        >
                          编辑权限
                        </button>
                        {client.status === 'active' && (
                          <button
                            type="button"
                            className="text-amber-600 hover:text-amber-800"
                            onClick={() => {
                              if (window.confirm(`吊销 ${client.name} 的 Token？吊销后立即失效。`)) {
                                revokeMutation.mutate(client.id);
                              }
                            }}
                          >
                            吊销
                          </button>
                        )}
                        <button
                          type="button"
                          className="text-red-600 hover:text-red-800"
                          onClick={() => {
                            if (window.confirm(`删除客户端 ${client.name}？此操作不可恢复。`)) {
                              deleteMutation.mutate(client.id);
                            }
                          }}
                        >
                          删除
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="pim-panel p-4">
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-sm font-bold text-slate-800">MCP 实时调用流水 (Activity Logs)</h2>
          <button
            type="button"
            className="pim-button-secondary px-3 py-1 text-xs"
            onClick={() => qc.invalidateQueries({ queryKey: ['mcp-activity'] })}
          >
            刷新
          </button>
        </div>
        {activityLogs.length === 0 ? (
          <p className="rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
            暂无调用记录
          </p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-slate-200 text-xs font-medium text-slate-500">
                <tr>
                  <th className="px-2 py-2">时间戳</th>
                  <th className="px-2 py-2">客户端名称</th>
                  <th className="px-2 py-2">Tool Name</th>
                  <th className="px-2 py-2">状态码</th>
                  <th className="px-2 py-2">耗时</th>
                  <th className="px-2 py-2">Arguments</th>
                </tr>
              </thead>
              <tbody>
                {activityLogs.map((entry: McpActivityLogEntry) => {
                  const statusClass = entry.statusCode >= 400
                    ? 'border-red-200 bg-red-50 text-red-700'
                    : entry.statusCode === 200
                      ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
                      : 'border-slate-200 bg-slate-100 text-slate-700';
                  return (
                    <tr key={`${entry.timestamp}-${entry.toolName}-${entry.clientName}`} className="border-b border-slate-100 hover:bg-slate-50">
                      <td className="px-2 py-2.5 text-xs text-slate-600 whitespace-nowrap">
                        {new Date(entry.timestamp).toLocaleString('zh-CN')}
                      </td>
                      <td className="px-2 py-2.5 text-sm text-slate-900">{entry.clientName}</td>
                      <td className="px-2 py-2.5">
                        <code className="rounded bg-slate-100 px-1.5 py-0.5 text-xs font-mono text-slate-800">
                          {entry.toolName}
                        </code>
                      </td>
                      <td className="px-2 py-2.5">
                        <span className={`inline-flex rounded-full border px-2 py-0.5 text-xs font-medium ${statusClass}`}>
                          {entry.statusCode}
                        </span>
                      </td>
                      <td className="px-2 py-2.5 text-xs text-slate-500">{entry.durationMs}ms</td>
                      <td className="px-2 py-2.5">
                        <span
                          className="block max-w-[200px] truncate font-mono text-xs text-slate-500"
                          title={entry.argumentsSummary || '—'}
                        >
                          {entry.argumentsSummary || '—'}
                        </span>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="pim-panel p-4">
        <h2 className="mb-3 text-sm font-bold text-slate-800">
          权限管理
          {editing && <span className="ml-2 font-normal text-slate-400">— {editing.name}</span>}
        </h2>
        {editing && draftPermissions ? (
          <div className="space-y-4">
            <PermissionEditor
              readTools={readTools}
              writeTools={writeTools}
              permissions={draftPermissions}
              onChange={setDraftPermissions}
            />
            <div className="flex gap-2">
              <button
                type="button"
                className="pim-button-primary px-4 py-2 text-sm"
                disabled={savePermissionsMutation.isPending}
                onClick={() =>
                  editing && draftPermissions && savePermissionsMutation.mutate({ id: editing.id, permissions: draftPermissions })
                }
              >
                {savePermissionsMutation.isPending ? '保存中...' : '保存权限'}
              </button>
              <button
                type="button"
                className="pim-button-secondary px-4 py-2 text-sm"
                onClick={() => {
                  setEditing(null);
                  setDraftPermissions(null);
                }}
              >
                取消
              </button>
            </div>
            {savePermissionsMutation.isError && (
              <p className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                保存失败：{savePermissionsMutation.error instanceof Error ? savePermissionsMutation.error.message : '请求失败'}
              </p>
            )}
          </div>
        ) : (
          <p className="rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
            选择上方一个客户端开始编辑权限。默认：读取全开、写入全关。
          </p>
        )}
      </section>

      <EditorDrawer
        open={createOpen}
        title="新建客户端"
        subtitle="生成一次性 Token，客户端用它连接 MCP（不涉及账号密码）"
        onClose={closeCreate}
        footer={
          createdToken ? (
            <button type="button" onClick={closeCreate} className="pim-button-primary px-4 py-2 text-sm">
              我已完成保存
            </button>
          ) : (
            <button
              type="button"
              className="pim-button-primary px-4 py-2 text-sm disabled:opacity-50"
              disabled={!createName.trim() || createMutation.isPending}
              onClick={() => createMutation.mutate(createName.trim())}
            >
              {createMutation.isPending ? '生成中...' : '生成 Token'}
            </button>
          )
        }
      >
        {!createdToken ? (
          <div className="space-y-3">
            <label className="block text-sm font-medium text-slate-700">
              客户端名称
              <input
                type="text"
                value={createName}
                onChange={event => setCreateName(event.target.value)}
                maxLength={80}
                placeholder="如 Hermes / Claude Code / Codex"
                className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-300 focus:outline-none"
              />
            </label>
            {createMutation.isError && (
              <p className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                {createMutation.error instanceof Error ? createMutation.error.message : '请求失败'}
              </p>
            )}
          </div>
        ) : (
          <div className="space-y-4">
            <p className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
              Token 只显示这一次，请立即保存。之后只能吊销重建。
            </p>
            <div className="rounded-lg border border-slate-200 bg-slate-50 p-3">
              <p className="text-xs font-semibold text-slate-500">一次性 Token</p>
              <p className="mt-1 break-all font-mono text-sm font-semibold text-slate-950">{createdToken}</p>
              <button
                type="button"
                onClick={() => copyToken(createdToken)}
                className="mt-2 text-xs font-semibold text-blue-600 hover:text-blue-800"
              >
                复制 Token
              </button>
            </div>
            <div className="rounded-lg border border-slate-200 bg-slate-50 p-3">
              <p className="text-xs font-semibold text-slate-500">连接配置示例（mcp.json）</p>
              <pre className="mt-1 overflow-x-auto font-mono text-[11px] leading-relaxed text-slate-700">{TOKEN_CONFIG_EXAMPLE}</pre>
              <button
                type="button"
                onClick={() => copyToken(TOKEN_CONFIG_EXAMPLE.replace('<PIM_MCP_TOKEN>', createdToken))}
                className="mt-2 text-xs font-semibold text-blue-600 hover:text-blue-800"
              >
                复制配置
              </button>
            </div>
          </div>
        )}
      </EditorDrawer>
    </div>
  );
}
