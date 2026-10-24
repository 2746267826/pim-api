import assert from 'node:assert/strict';
import { authFailureMessage, readAuthResponse } from '../../src/client-web/src/auth/authApi';

async function main() {
  const unauthorized = new Response('', { status: 401 });
  const unauthorizedBody = await readAuthResponse(unauthorized);

  assert.equal(unauthorizedBody, null);
  assert.equal(authFailureMessage('login', unauthorized, unauthorizedBody), '用户名或密码不正确');

  const conflict = new Response(
    JSON.stringify({
      code: 1003,
      message: '用户名已存在',
      data: null,
      timestamp: new Date('2026-05-28T00:00:00Z').toISOString(),
    }),
    { status: 409 },
  );
  const conflictBody = await readAuthResponse(conflict);

  assert.equal(conflictBody?.message, '用户名已存在');
  assert.equal(authFailureMessage('register', conflict, conflictBody), '用户名已存在');
}

void main();
