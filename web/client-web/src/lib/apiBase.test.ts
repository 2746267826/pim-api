import { beforeEach, describe, expect, it } from 'vitest'
import {
  apiUrl,
  candidateUrl,
  getApiBase,
  getApiBaseHistory,
  needsServerSetup,
  normalizeApiBaseInput,
  setApiBase,
} from './apiBase'
import { STORAGE_KEYS, remove, setJSON } from './storage'

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
    setApiBase('http://192.168.1.10:5858///')
    expect(getApiBase()).toBe('http://192.168.1.10:5858')
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
    setApiBase('http://10.0.0.2:5858')
    expect(apiUrl('/api/v1/status')).toBe('http://10.0.0.2:5858/api/v1/status')
  })

  it('candidateUrl 不影响当前生效地址', () => {
    setApiBase('http://a.example')
    expect(candidateUrl('http://b.example/', '/api/version')).toBe('http://b.example/api/version')
    expect(getApiBase()).toBe('http://a.example')
  })
})

describe('历史记录', () => {
  it('去重并置顶，最多 5 条', () => {
    for (const b of ['http://a', 'http://b', 'http://c', 'http://d', 'http://e', 'http://f']) {
      setApiBase(b)
    }
    let hist = getApiBaseHistory()
    expect(hist).toEqual(['http://f', 'http://e', 'http://d', 'http://c', 'http://b'])
    setApiBase('http://d')
    hist = getApiBaseHistory()
    expect(hist[0]).toBe('http://d')
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

describe('needsServerSetup（壳内未配置 → 首启向导）', () => {
  it('非壳环境恒为 false', () => {
    setApiBase('')
    expect(needsServerSetup()).toBe(false)
  })
})
