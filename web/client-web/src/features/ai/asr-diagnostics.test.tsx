// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { formatAsrSeconds, parseAsrDiagnostics } from './asr-diagnostics'
import { AsrResultInsights } from './asr-result-insights'

function snapshot(options: {
  canonicalAudioTokens?: number
  directAudioTokens?: number
  flags?: Record<string, boolean>
  transcript?: Record<string, unknown>
  finishReason?: string
}) {
  return JSON.stringify({
    recordFormat: 'mimo-multimodal-v1',
    contentSource: 'content',
    finishReason: options.finishReason ?? 'stop',
    flags: options.flags ?? {},
    transcript: options.transcript ?? { text: '逐字转写', segments: [], issues: [] },
    upstream: {
      choices: [{ message: { content: '逐字转写' }, finish_reason: 'stop' }],
      usage: {
        prompt_tokens_details: options.canonicalAudioTokens == null ? {} : { audio_tokens: options.canonicalAudioTokens },
        ...(options.directAudioTokens == null ? {} : { audio_tokens: options.directAudioTokens }),
      },
    },
  })
}

afterEach(cleanup)

describe('ASR结果诊断', () => {
  it('只接受canonical audio_tokens，direct正数不能救回canonical 0或缺失', () => {
    const zero = parseAsrDiagnostics(snapshot({ canonicalAudioTokens: 0, directAudioTokens: 100 }))
    expect(zero?.audioTokens).toBe(0)
    expect(zero?.ignoredDirectAudioTokens).toBe(100)

    const missing = parseAsrDiagnostics(snapshot({ directAudioTokens: 100 }))
    expect(missing?.audioTokens).toBeNull()
    expect(missing?.ignoredDirectAudioTokens).toBe(100)

    const valid = parseAsrDiagnostics(snapshot({ canonicalAudioTokens: 7, directAudioTokens: 100 }))
    expect(valid?.audioTokens).toBe(7)
  })

  it('展示红色缺token、空/重复/截断和时间戳回退警告', () => {
    render(<AsrResultInsights rawJson={snapshot({
      canonicalAudioTokens: 0,
      directAudioTokens: 100,
      finishReason: 'incomplete:truncation',
      flags: { empty: true, repetition: true, truncation: true, timestampFallback: true },
    })} />)

    expect(screen.getByRole('heading', { name: 'ASR结果核验' })).toBeTruthy()
    expect(screen.getByText('音频处理未确认')).toBeTruthy()
    expect(screen.getByText(/usage\.audio_tokens=100/)).toBeTruthy()
    expect(screen.getByText('空转写')).toBeTruthy()
    expect(screen.getByText('重复异常')).toBeTruthy()
    expect(screen.getByText('输出截断')).toBeTruthy()
    expect(screen.getByText(/时间戳为回退状态/)).toBeTruthy()
    expect(screen.getByText('音频处理未确认').closest('[role="alert"]')?.className).toContain('text-crit')
  })

  it('展示真实起始-结束分段并计算重叠后的有效覆盖，保留解析问题', () => {
    const diagnostics = parseAsrDiagnostics(snapshot({
      canonicalAudioTokens: 9,
      flags: { formatDetected: true, hasOverlaps: true },
      transcript: {
        text: '0-4 | 教师: 第一段\n2-7 | 学生: 第二段\n9-10 | 教师: 第三段',
        segments: [
          { startSeconds: 0, endSeconds: 4, speaker: '教师', text: '第一段' },
          { startSeconds: 2, endSeconds: 7, speaker: '学生', text: '第二段' },
          { startSeconds: 9, endSeconds: 10, speaker: '教师', text: '第三段' },
        ],
        coverageSeconds: 8,
        issues: ['line 3: sample issue'],
      },
    }))
    expect(diagnostics?.coverageSeconds).toBe(8)
    expect(diagnostics?.segments).toHaveLength(3)
    expect(formatAsrSeconds(65.2)).toBe('1:05.2')

    render(<AsrResultInsights rawJson={snapshot({
      canonicalAudioTokens: 9,
      flags: { formatDetected: true, hasOverlaps: true },
      transcript: {
        text: '',
        segments: [
          { startSeconds: 0, endSeconds: 4, speaker: '教师', text: '第一段' },
          { startSeconds: 2, endSeconds: 7, speaker: '学生', text: '第二段' },
        ],
        coverageSeconds: 7,
        issues: ['line 3: sample issue'],
      },
    })} />)
    expect(screen.getByText('分段结果（2）')).toBeTruthy()
    expect(screen.getByText('第一段')).toBeTruthy()
    expect(screen.getByText('第二段')).toBeTruthy()
    expect(screen.getByText(/模型估算，非强制对齐/)).toBeTruthy()
    expect(screen.getByText('解析问题（1）')).toBeTruthy()
  })

  it('旧ASR专用响应没有canonical audio_tokens时保持旧语义，不误报多模态失败', () => {
    const raw = JSON.stringify({
      choices: [{ message: { content: '旧接口逐字转写' }, finish_reason: 'stop' }],
      usage: { seconds: 12 },
    })
    expect(parseAsrDiagnostics(raw)?.multimodalWrapper).toBe(false)
    render(<AsrResultInsights rawJson={raw} presetId="mimo-asr" />)
    expect(screen.getByText('非多模态ASR记录')).toBeTruthy()
    expect(screen.queryByText('音频处理未确认')).toBeNull()
    expect(screen.queryByText('ASR结果核验')).toBeNull()
  })

  it('显式MiMo raw completion从choices读取length截断，并在content空白时回退reasoning', () => {
    const raw = JSON.stringify({
      recordFormat: 'mimo-multimodal-v1',
      flags: {},
      transcript: { text: '   ', segments: [], issues: [] },
      upstream: {
        choices: [{ message: { content: '   ', reasoning_content: '回退转写' }, finish_reason: 'length' }],
        usage: { prompt_tokens_details: { audio_tokens: 2 } },
      },
    })
    const parsed = parseAsrDiagnostics(raw)
    expect(parsed?.transcriptText).toBe('回退转写')
    expect(parsed?.empty).toBe(false)
    expect(parsed?.truncation).toBe(true)
    render(<AsrResultInsights rawJson={raw} />)
    expect(screen.getByText('输出截断')).toBeTruthy()
    expect(screen.queryByText('空转写')).toBeNull()
  })

  it('fallback分段乱序时排序后计算区间并集，并识别gap/overlap', () => {
    const parsed = parseAsrDiagnostics(snapshot({
      canonicalAudioTokens: 9,
      transcript: {
        text: '9-10 | 教师: 第三段\n2-7 | 学生: 第二段\n0-4 | 教师: 第一段',
        segments: [],
        issues: [],
      },
    }))
    expect(parsed?.segments.map((segment) => segment.text)).toEqual(['第一段', '第二段', '第三段'])
    expect(parsed?.coverageSeconds).toBe(8)
    expect(parsed?.hasOverlaps).toBe(true)
    expect(parsed?.hasGaps).toBe(true)
  })

  it('长结果分段按100条分页，翻页可完整查看且不一次性渲染全部', async () => {
    const segments = Array.from({ length: 205 }, (_, index) => ({
      startSeconds: index,
      endSeconds: index + 0.5,
      speaker: '教师',
      text: `题目${index}`,
    }))
    render(<AsrResultInsights rawJson={snapshot({
      canonicalAudioTokens: 9,
      transcript: { text: '', segments, issues: [] },
    })} />)
    expect(screen.getByText('第 1 / 3 页')).toBeTruthy()
    expect(screen.getByText('题目0', { exact: true })).toBeTruthy()
    expect(screen.queryByText('题目100', { exact: true })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: '下一页分段' }))
    expect(screen.getByText('第 2 / 3 页')).toBeTruthy()
    expect(screen.getByText('题目100', { exact: true })).toBeTruthy()
    expect(screen.queryByText('题目0', { exact: true })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: '下一页分段' }))
    expect(screen.getByText('第 3 / 3 页')).toBeTruthy()
    expect(screen.getByText('题目200', { exact: true })).toBeTruthy()
    expect(screen.getByText('题目204', { exact: true })).toBeTruthy()
    expect((screen.getByRole('button', { name: '下一页分段' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('纯文本无分段时明确标记为回退，且畸形快照不伪报诊断', () => {
    const fallback = parseAsrDiagnostics(snapshot({
      canonicalAudioTokens: 3,
      transcript: { text: '只有逐字文本，没有可信时间戳', segments: [], issues: [] },
    }))
    expect(fallback?.timestampFallback).toBe(true)
    expect(fallback?.segments).toHaveLength(0)
    expect(parseAsrDiagnostics('{not-json')).toBeNull()
    expect(parseAsrDiagnostics('{}')).toBeNull()

    const view = render(<AsrResultInsights rawJson="{}" />)
    expect(screen.getByText('ASR结果无法核验')).toBeTruthy()
    view.rerender(<AsrResultInsights rawJson="{not-json" />)
    expect(screen.getByText('ASR结果无法核验')).toBeTruthy()
  })

  it('解包AiAsrCallService实际asr-call-response-v1.parsed并保留mimo分段', () => {
    const raw = JSON.stringify({
      recordFormat: 'asr-call-response-v1',
      parsed: {
        recordFormat: 'mimo-multimodal-v1',
        model: 'mimo-v2.6-flash',
        contentSource: 'content',
        finishReasonRaw: 'stop',
        finishReason: 'stop',
        audioTokens: 5,
        flags: { empty: false, repetition: false, truncation: false, timestampFallback: false, formatDetected: true, hasGaps: false, hasOverlaps: false },
        transcript: {
          text: '0-1 | 说话人: 你好',
          coverageSeconds: 1,
          segments: [{ startSeconds: 0, endSeconds: 1, speaker: '说话人', text: '你好' }],
          issues: [],
        },
        upstream: {
          choices: [{ message: { content: '0-1 | 说话人: 你好' }, finish_reason: 'stop' }],
          usage: { prompt_tokens: 8, completion_tokens: 4, total_tokens: 12, prompt_tokens_details: { audio_tokens: 5 } },
        },
      },
      raw: null,
      sources: { parsed: 'preset-parser', raw: 'parsed.upstream' },
    })

    const parsed = parseAsrDiagnostics(raw)
    expect(parsed?.auditWrapper).toBe(true)
    expect(parsed?.parsedAvailable).toBe(true)
    expect(parsed?.multimodalWrapper).toBe(true)
    expect(parsed?.audioTokens).toBe(5)
    expect(parsed?.transcriptText).toBe('0-1 | 说话人: 你好')
    expect(parsed?.segments).toEqual([{ startSeconds: 0, endSeconds: 1, speaker: '说话人', text: '你好' }])

    render(<AsrResultInsights rawJson={raw} presetId="mimo-multimodal" />)
    expect(screen.getByRole('heading', { name: 'ASR结果核验' })).toBeTruthy()
    expect(screen.getByText('audio_tokens 5')).toBeTruthy()
    expect(screen.queryByText('非多模态ASR记录')).toBeNull()
  })

  it('外层成功wrapper缺少canonical tokens时仍红色失败', () => {
    const raw = JSON.stringify({
      recordFormat: 'asr-call-response-v1',
      parsed: {
        recordFormat: 'mimo-multimodal-v1',
        contentSource: 'content',
        finishReason: 'stop',
        audioTokens: null,
        flags: {},
        transcript: { text: '逐字文本', segments: [], issues: [] },
        upstream: { choices: [{ message: { content: '逐字文本' }, finish_reason: 'stop' }], usage: { total_tokens: 9 } },
      },
      raw: null,
      sources: { parsed: 'preset-parser', raw: 'same-as-parsed' },
    })
    const parsed = parseAsrDiagnostics(raw)
    expect(parsed?.auditWrapper).toBe(true)
    expect(parsed?.audioTokens).toBeNull()
    render(<AsrResultInsights rawJson={raw} presetId="mimo-multimodal" />)
    expect(screen.getByText('音频处理未确认')).toBeTruthy()
    expect(screen.queryByText('基础语义检查通过')).toBeNull()
  })

  it('外层wrapper解析length并使用parsed content空白后的reasoning fallback', () => {
    const raw = JSON.stringify({
      recordFormat: 'asr-call-response-v1',
      parsed: {
        recordFormat: 'mimo-multimodal-v1',
        contentSource: 'reasoning_content',
        finishReasonRaw: 'length',
        finishReason: 'length',
        audioTokens: 2,
        flags: {},
        transcript: { text: '   ', segments: [], issues: [] },
        upstream: { choices: [{ message: { content: '   ', reasoning_content: '回退转写' }, finish_reason: 'length' }], usage: { prompt_tokens_details: { audio_tokens: 2 } } },
      },
      raw: null,
      sources: { parsed: 'preset-parser', raw: 'parsed.upstream' },
    })
    const parsed = parseAsrDiagnostics(raw)
    expect(parsed?.transcriptText).toBe('回退转写')
    expect(parsed?.empty).toBe(false)
    expect(parsed?.truncation).toBe(true)
    render(<AsrResultInsights rawJson={raw} presetId="mimo-multimodal" />)
    expect(screen.getByText('输出截断')).toBeTruthy()
    expect(screen.queryByText('空转写')).toBeNull()
  })

  it('只读parsed canonical，不让outer/raw wire的usage.audio_tokens别名救回0', () => {
    const raw = JSON.stringify({
      recordFormat: 'asr-call-response-v1',
      parsed: {
        recordFormat: 'mimo-multimodal-v1',
        contentSource: 'content',
        finishReason: 'stop',
        audioTokens: 0,
        flags: {},
        transcript: { text: '逐字文本', segments: [], issues: [] },
        upstream: { choices: [{ message: { content: '逐字文本' }, finish_reason: 'stop' }],
          usage: { audio_tokens: 100, prompt_tokens_details: { audio_tokens: 0 } } },
      },
      raw: { choices: [{ message: { content: 'raw-wire' } }], usage: { audio_tokens: 999 } },
      sources: { parsed: 'preset-parser', raw: 'http-wire' },
    })
    const parsed = parseAsrDiagnostics(raw)
    expect(parsed?.audioTokens).toBe(0)
    expect(parsed?.ignoredDirectAudioTokens).toBe(100)
    expect(parsed?.transcriptText).toBe('逐字文本')
    render(<AsrResultInsights rawJson={raw} presetId="mimo-multimodal" />)
    expect(screen.getByText(/usage\.audio_tokens=100/)).toBeTruthy()
    expect(screen.queryByText(/999/)).toBeNull()
    expect(screen.getByText('音频处理未确认')).toBeTruthy()
  })

  it('失败wrapper在parsed=null时从raw wire保持不可核验，不冒充成功', () => {
    const raw = JSON.stringify({
      recordFormat: 'asr-call-response-v1',
      parsed: null,
      raw: { choices: [{ message: { content: 'unverified' }, finish_reason: 'stop' }], usage: { prompt_tokens: 8, total_tokens: 9 } },
      sources: { parsed: 'none', raw: 'http-wire' },
    })
    const parsed = parseAsrDiagnostics(raw)
    expect(parsed?.auditWrapper).toBe(true)
    expect(parsed?.parsedAvailable).toBe(false)
    expect(parsed?.audioTokens).toBeNull()
    expect(parsed?.transcriptText).toBe('unverified')
    render(<AsrResultInsights rawJson={raw} presetId="mimo-multimodal" />)
    expect(screen.getByText('音频处理未确认')).toBeTruthy()
    expect(screen.queryByText('基础语义检查通过')).toBeNull()
  })
})
