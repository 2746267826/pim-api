import { apiDelete, apiGet, apiPost, apiPut } from './client';
import type { ApiResponse } from '../types';
import type { McpActivityLogEntry, McpCatalog, McpClient, McpClientCreateResult, McpClientUpdateRequest } from '../types';

export const mcpApiPaths = {
  list: '/mcp/clients',
  create: '/mcp/clients',
  client: (id: string) => `/mcp/clients/${id}`,
  revoke: (id: string) => `/mcp/clients/${id}/revoke`,
  catalog: '/mcp/catalog',
  activity: '/mcp/activity',
} as const;

export async function listMcpClients(): Promise<McpClient[]> {
  const response = await apiGet<ApiResponse<McpClient[]>>(mcpApiPaths.list);
  return response.data;
}

export async function createMcpClient(name: string): Promise<McpClientCreateResult> {
  const response = await apiPost<ApiResponse<McpClientCreateResult>>(mcpApiPaths.create, { name });
  return response.data;
}

export async function updateMcpClient(id: string, request: McpClientUpdateRequest): Promise<McpClient> {
  const response = await apiPut<ApiResponse<McpClient>>(mcpApiPaths.client(id), request);
  return response.data;
}

export async function revokeMcpClient(id: string): Promise<McpClient> {
  const response = await apiPost<ApiResponse<McpClient>>(mcpApiPaths.revoke(id));
  return response.data;
}

export async function deleteMcpClient(id: string): Promise<void> {
  await apiDelete<ApiResponse<unknown>>(mcpApiPaths.client(id));
}

export async function getMcpCatalog(): Promise<McpCatalog> {
  const response = await apiGet<ApiResponse<McpCatalog>>(mcpApiPaths.catalog);
  return response.data;
}

export async function getMcpActivity(): Promise<McpActivityLogEntry[]> {
  const response = await apiGet<ApiResponse<McpActivityLogEntry[]>>(mcpApiPaths.activity);
  return response.data;
}
