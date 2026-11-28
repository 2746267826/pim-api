import { beforeEach, describe, expect, it } from 'vitest'
import {
  apiUrl,
  candidateUrl,
  getApiBase,
  getApiBaseHistory,
  needsServerSetup,
  normalizeApiBaseInput,
  setApiBase,
  validateApiBaseInput,
} from './apiBase'
import { STORAGE_KEYS, remove, setJSON, setString } from './storage'

beforeEach(() => {
  remove(STORAGE_KEYS.apiBase)
  remove(STORAGE_KEYS.apiBaseHistory)
})

describe('normalizeApiBaseInput（base 恒为源地址）', () => {
  it('剥离误带的 /api/v1 后缀（用户常粘贴完整 API URL）', () => {
    expect(normalizeApiBaseInput('https://pim.example.com:15860/api/v1')).toBe('https://pim.example.com:15860')
  })

  it('大小写不敏感且连带尾斜杠一起处理', () => {
    expect(normalizeApiBaseInput('https://x.example/API/V1/')).toBe('https://x.example')
    expect(normalizeApiBaseInput('http://10.0.0.2:5858/api/')).toBe('http://10.0.0.2:5858')
  })

  it('纯源地址与空值不变', () => {
    expect(normalizeApiBaseInput('http://192.168.1.10:5858')).toBe('http://192.168.1.10:5858')
    expect(normalizeApiBaseInput('  ')).toBe('')
  })

  it('setApiBase 存储归一化后的值', () => {
    setApiBase('https://pim.example.com:15860/api/v1')
    expect(getApiBase()).toBe('https://pim.example.com:15860')
  })

  it('candidateUrl 同样归一化（测试连接用）', () => {
    expect(candidateUrl('https://x.example/api/v1/', '/api/version')).toBe('https://x.example/api/version')
  })
})

describe('setApiBase / getApiBase', () => {
  it('去除尾部斜杠', () => {
    setApiBase('https://pim.example.com:5858///')
    expect(getApiBase()).toBe('https://pim.example.com:5858')
  })

  it('空值表示同源', () => {
    setApiBase('  ')
    expect(getApiBase()).toBe('')
  })

  it('无协议地址抛错', () => {
    expect(() => setApiBase('192.168.1.10:5858')).toThrow('协议')
  })
})

describe('apiUrl / candidateUrl', () => {
  it('同源时原样返回相对路径', () => {
    setApiBase('')
    expect(apiUrl('/api/v1/status')).toBe('/api/v1/status')
  })

  it('配置后拼接绝对地址', () => {
    setApiBase('http://127.0.0.1:5858')
    expect(apiUrl('/api/v1/status')).toBe('http://127.0.0.1:5858/api/v1/status')
  })

  it('candidateUrl 不影响当前生效地址', () => {
    setApiBase('https://a.example')
    expect(candidateUrl('https://b.example/', '/api/version')).toBe('https://b.example/api/version')
    expect(getApiBase()).toBe('https://a.example')
  })
})

describe('历史记录', () => {
  it('去重并置顶，最多 5 条', () => {
    for (const b of ['https://a.example', 'https://b.example', 'https://c.example', 'https://d.example', 'https://e.example', 'https://f.example']) {
      setApiBase(b)
    }
    let hist = getApiBaseHistory()
    expect(hist).toEqual(['https://f.example', 'https://e.example', 'https://d.example', 'https://c.example', 'https://b.example'])
    setApiBase('https://d.example')
    hist = getApiBaseHistory()
    expect(hist[0]).toBe('https://d.example')
    expect(hist).toHaveLength(5)
  })

  it('读取时归一化历史条目（修复带后缀的旧数据）', () => {
    setJSON(STORAGE_KEYS.apiBaseHistory, [
      'https://pim.example.com:15860/api/v1',
      'https://pim.example.com:15860',
      'bad value',
    ])
    expect(getApiBaseHistory()).toEqual(['https://pim.example.com:15860'])
  })
})

describe('validateApiBaseInput（Q04 传输门禁）', () => {
  it('远程 HTTP 在保存和测试连接前被阻止', () => {
    const validation = validateApiBaseInput('http://notes.example.com:5858/api/v1')

    expect(validation.ok).toBe(false)
    expect(validation.reasonCode).toBe('remote-http-blocked')
    expect(validation.message).toContain('HTTPS')
    expect(() => setApiBase('http://notes.example.com:5858')).toThrow('HTTPS')
  })

  it('本机回环 HTTP 保留为开发例外', () => {
    for (const base of ['http://127.0.0.1:5858', 'http://localhost:5858', 'http://[::1]:5858', 'http://10.0.2.2:5858']) {
      const validation = validateApiBaseInput(base)
      expect(validation.ok, base).toBe(true)
      expect(validation.isLoopback, base).toBe(true)
      expect(() => setApiBase(base)).not.toThrow()
      expect(getApiBase()).toBe(base.replace(/\/$/, ''))
    }
  })

  it('局域网 IP 仍按远程地址处理', () => {
    expect(validateApiBaseInput('http://192.168.1.15:5858')).toMatchObject({
      ok: false,
      reasonCode: 'remote-http-blocked',
    })
  })

  it('HTTPS 远程地址允许保存', () => {
    const validation = validateApiBaseInput('https://notes.example.com/api/v1/')

    expect(validation).toMatchObject({
      ok: true,
      value: 'https://notes.example.com',
      isLoopback: false,
    })
    expect(() => setApiBase(validation.value)).not.toThrow()
  })

  it('拒绝账号、查询参数和片段，避免把凭据写进基址', () => {
    for (const raw of [
      'https://user:secret@notes.example.com',
      'https://notes.example.com?token=secret',
      'https://notes.example.com#fragment',
    ]) {
      expect(validateApiBaseInput(raw).ok, raw).toBe(false)
    }
  })

  it('历史遗留的远程 HTTP 不会再被读取为生效基址或历史项', () => {
    setString(STORAGE_KEYS.apiBase, 'http://notes.example.com:5858')
    setJSON(STORAGE_KEYS.apiBaseHistory, [
      'http://notes.example.com:5858',
      'https://notes.example.com',
      'http://127.0.0.1:5858',
    ])

    expect(getApiBase()).toBe('')
    expect(getApiBaseHistory()).toEqual(['https://notes.example.com', 'http://127.0.0.1:5858'])
  })
})

describe('needsServerSetup（壳内未配置 → 首启向导）', () => {
  it('非壳环境恒为 false', () => {
    setApiBase('')
    expect(needsServerSetup()).toBe(false)
  })
})
