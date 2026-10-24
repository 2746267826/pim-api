import assert from 'node:assert/strict';
import { mcpApiPaths } from '../../src/client-web/src/api/mcp';

const id = '11111111-1111-1111-1111-111111111111';

assert.equal(mcpApiPaths.list, '/mcp/clients');
assert.equal(mcpApiPaths.create, '/mcp/clients');
assert.equal(mcpApiPaths.client(id), `/mcp/clients/${id}`);
assert.equal(mcpApiPaths.revoke(id), `/mcp/clients/${id}/revoke`);
assert.equal(mcpApiPaths.catalog, '/mcp/catalog');
console.log('mcpApiPath OK');
