export type McpCatalogTool = {
  name: string
  defaultEnabled?: boolean
}

/**
 * Builds the permission dialog state from the server catalog. Saved entries are accepted only
 * for catalog-known tools, so an old client's unknown read key can never stay enabled through
 * the UI; missing keys use the catalog default (native-note reads default to false).
 */
export function initialMcpPermissions(
  catalog: readonly McpCatalogTool[],
  saved: Record<string, boolean> | undefined,
): Record<string, boolean> {
  const known = new Set(catalog.map((tool) => tool.name))
  const result: Record<string, boolean> = {}
  for (const tool of catalog) result[tool.name] = tool.defaultEnabled ?? false
  for (const [name, enabled] of Object.entries(saved ?? {})) {
    if (known.has(name)) result[name] = enabled
  }
  return result
}
