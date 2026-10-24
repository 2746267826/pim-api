/*
 * 自验/QA 脚本共用的环境变量加载器。
 *
 * 敏感值（服务器地址、测试账号）不再硬编码，统一放在 client-web/.env（已 gitignore）。
 * 复制 .env.example 为 .env 并填写；缺失时下面会明确报错，而不是静默用错地址。
 */
import { existsSync } from 'node:fs'
import path from 'node:path'

const envPath = path.resolve(import.meta.dirname, '..', '.env')
// loadEnvFile 不覆盖已存在的环境变量，故 shell 里显式指定的值优先。
if (existsSync(envPath)) process.loadEnvFile(envPath)

/** 读取必需的环境变量；缺失即抛错并给出修复提示。 */
export function requireEnv(name) {
  const value = process.env[name]
  if (!value) {
    throw new Error(
      `缺少环境变量 ${name}。请在 client-web/.env 中设置（可从 .env.example 复制），或临时用 shell 传入。`,
    )
  }
  return value
}

/** 测试 API 基地址（去掉多余的尾部斜杠）。 */
export function apiBase() {
  return requireEnv('PIM_API_TARGET').replace(/\/+$/, '')
}

/** 测试账号凭据。 */
export function testCredentials() {
  return { username: requireEnv('PIM_TEST_USER'), password: requireEnv('PIM_TEST_PASS') }
}
