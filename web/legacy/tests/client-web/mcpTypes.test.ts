import assert from 'node:assert/strict';
import type { McpCatalog, McpClient, McpClientCreateResult, McpPermissions, McpToolInfo } from '../../src/client-web/src/types';
import { createMcpClient, listMcpClients, updateMcpClient, revokeMcpClient, deleteMcpClient, getMcpCatalog } from '../../src/client-web/src/api/mcp';

const perms: McpPermissions = { read: { get_tasks: true }, write: { create_task: false } };
const tool: McpToolInfo = { name: 'create_task', group: 'calendar.tasks', description: 'x', isWrite: true };
const catalog: McpCatalog = { read: [], write: [tool] };
const client: McpClient = {
  id: '11111111-1111-1111-1111-111111111111',
  name: 'Hermes',
  status: 'active',
  tokenPrefix: 'pim_mcp_ab12',
  permissions: perms,
  createdAt: '2026-09-01T00:00:00Z',
  revokedAt: null,
  lastSeenAt: null,
  callCount: 0,
  writeCallCount: 0,
  lastTool: null,
  online: false,
  createdByUsername: 'alice',
};
const result: McpClientCreateResult = { client, token: 'pim_mcp_abcdef' };

assert.equal(catalog.write.length, 1);
assert.equal(client.status, 'active');
assert.equal(result.token.startsWith('pim_mcp_'), true);
assert.equal(perms.write.create_task, false);

const fns: unknown[] = [createMcpClient, listMcpClients, updateMcpClient, revokeMcpClient, deleteMcpClient, getMcpCatalog];
assert.equal(fns.every(f => typeof f === 'function'), true);
console.log('mcpTypes OK');
