import type { ApiResponse, AuthResponse } from '../types';

export type AuthAction = 'login' | 'register';
export type AuthApiResponse = ApiResponse<AuthResponse>;

export async function readAuthResponse(response: Response): Promise<AuthApiResponse | null> {
  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return null;
  }

  try {
    return await response.json() as AuthApiResponse;
  } catch {
    return null;
  }
}

export function authFailureMessage(
  action: AuthAction,
  response: Response,
  body: AuthApiResponse | null,
): string {
  if (body?.message) return body.message;
  if (response.status === 401) return '用户名或密码不正确';
  if (response.status === 429) return '登录尝试过多，请稍后再试';
  return action === 'register' ? '注册失败，请稍后再试' : '登录失败，请稍后再试';
}
