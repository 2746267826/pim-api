#!/usr/bin/env node
/**
 * #312 回归测试：打包器必须产出「规范 zip」。
 *
 * 核心断言（对应 issue 的实测后果）：
 *   1. 条目名一律用 `/`，绝不出现 `\`——Firefox 与 `unzip` 都按规范解析，
 *      反斜杠会让 manifest 引用的 `src/background/main.js` 找不到（报「损坏」）；
 *   2. 归档可被标准工具（`unzip -t`）判定为完整无损；
 *   3. 中文/非 ASCII 内容与文件名往返无损；
 *   4. 目录结构完整、可被 zipfile 逐个读出内容一致。
 *
 * 运行：node --test scripts/ci/test-zip-writer.mjs
 */
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { crc32, listFilesRecursive, readZipEntryNames, readZipEntry, toEntryName, writeZipFromDirectory } from './zip-writer.mjs'

function makeTree() {
  const root = mkdtempSync(join(tmpdir(), 'pim-zip-'))
  mkdirSync(join(root, 'src', 'background'), { recursive: true })
  mkdirSync(join(root, 'media', 'logo'), { recursive: true })
  writeFileSync(join(root, 'manifest.json'), JSON.stringify({ name: '测试插件', manifest_version: 2 }))
  writeFileSync(join(root, 'src', 'background', 'main.js'), 'console.log("background")\n')
  writeFileSync(join(root, 'media', 'logo', 'logo-128.png'), Buffer.from([0x89, 0x50, 0x4e, 0x47, 0, 1, 2, 3]))
  // 中文文件名：验证 UTF-8 条目名往返。
  writeFileSync(join(root, 'media', '说明.txt'), '中文内容 / content\n')
  return root
}

test('toEntryName 把平台分隔符统一成斜杠', () => {
  assert.equal(toEntryName(join('src', 'background', 'main.js')), 'src/background/main.js')
  assert.equal(toEntryName('src\\background\\main.js'.split('\\').join('/')), 'src/background/main.js')
  assert.ok(!toEntryName(join('a', 'b', 'c')).includes('\\'))
})

test('listFilesRecursive 列出全部文件且不含目录', () => {
  const root = makeTree()
  try {
    const files = listFilesRecursive(root).map(toEntryName).sort()
    assert.deepEqual(files, [
      'manifest.json',
      'media/logo/logo-128.png',
      'media/说明.txt',
      'src/background/main.js',
    ])
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('crc32 与已知向量一致', () => {
  // 标准测试向量：CRC32("123456789") = 0xCBF43926
  assert.equal(crc32(Buffer.from('123456789')), 0xcbf43926)
  assert.equal(crc32(Buffer.alloc(0)), 0)
})

test('打包产物条目名不含反斜杠（#312 核心缺陷）', () => {
  const root = makeTree()
  const zipPath = join(mkdtempSync(join(tmpdir(), 'pim-zipout-')), 'browser-extension.zip')
  try {
    const result = writeZipFromDirectory(root, zipPath)
    const withBackslash = result.entryNames.filter((name) => name.includes('\\'))
    assert.deepEqual(withBackslash, [], `条目名不得含反斜杠：${withBackslash.join(', ')}`)
    assert.equal(result.entryCount, 4)
    assert.ok(result.bytes > 0)
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(join(zipPath, '..'), { recursive: true, force: true })
  }
})

test('unzip 能识别为完整归档、无分隔符告警（issue 里的实测工具）', () => {
  const root = makeTree()
  const zipPath = join(mkdtempSync(join(tmpdir(), 'pim-zipout-')), 'browser-extension.zip')
  try {
    writeZipFromDirectory(root, zipPath)

    // `unzip -t`：完整性检查。既有实现（反斜杠）会在这里告警。
    const listing = execFileSync('unzip', ['-l', zipPath], { encoding: 'utf8' })
    assert.ok(!/backslash/i.test(listing), `unzip 报告了反斜杠分隔符警告：\n${listing}`)
    assert.ok(listing.includes('src/background/main.js'), '应能按规范路径列出条目')

    const test = execFileSync('unzip', ['-t', zipPath], { encoding: 'utf8' })
    assert.ok(/No errors detected/i.test(test), `unzip -t 未通过：\n${test}`)
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(join(zipPath, '..'), { recursive: true, force: true })
  }
})

test('解包后内容与目录结构往返一致（含中文文件名）', () => {
  const root = makeTree()
  const zipDir = mkdtempSync(join(tmpdir(), 'pim-zipout-'))
  const zipPath = join(zipDir, 'browser-extension.zip')
  try {
    writeZipFromDirectory(root, zipPath)

    // 按 zip 规范自带的 UTF-8 标志位读取条目内容，而不是 shell 调用 `unzip`：
    // `unzip` 是否还原非 ASCII 名字取决于运行环境的 locale（CI 上常见 C locale，
    // 会把中文条目名写成乱码）。浏览器/规范实现按 UTF-8 解释，这里与之一致。
    const manifest = JSON.parse(readZipEntry(zipPath, 'manifest.json').toString('utf8'))
    assert.equal(manifest.manifest_version, 2)
    assert.equal(manifest.name, '测试插件')

    assert.equal(
      readZipEntry(zipPath, 'src/background/main.js').toString('utf8'),
      readFileSync(join(root, 'src', 'background', 'main.js'), 'utf8'),
    )
    assert.equal(
      readZipEntry(zipPath, 'media/说明.txt').toString('utf8'),
      readFileSync(join(root, 'media', '说明.txt'), 'utf8'),
    )
    assert.deepEqual(
      readZipEntry(zipPath, 'media/logo/logo-128.png'),
      readFileSync(join(root, 'media', 'logo', 'logo-128.png')),
    )
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zipDir, { recursive: true, force: true })
  }
})

test('大文件走 deflate 且往返无损', () => {
  const root = mkdtempSync(join(tmpdir(), 'pim-zip-'))
  const zipDir = mkdtempSync(join(tmpdir(), 'pim-zipout-'))
  const zipPath = join(zipDir, 'big.zip')
  const extractDir = mkdtempSync(join(tmpdir(), 'pim-zipextract-'))
  try {
    // 高度可压缩的内容：压缩后应明显小于原始大小。
    const payload = 'a'.repeat(500_000)
    writeFileSync(join(root, 'bundle.js'), payload)

    const result = writeZipFromDirectory(root, zipPath)
    assert.equal(result.entryCount, 1)
    assert.ok(result.bytes < payload.length / 10, `压缩无效：${result.bytes} bytes`)

    assert.equal(readZipEntry(zipPath, 'bundle.js').toString('utf8'), payload)
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zipDir, { recursive: true, force: true })
    rmSync(extractDir, { recursive: true, force: true })
  }
})

test('readZipEntryNames 能读回自己写的条目（Windows 校验路径不依赖 unzip）', () => {
  const root = makeTree()
  const zipDir = mkdtempSync(join(tmpdir(), 'pim-zipout-'))
  const zipPath = join(zipDir, 'browser-extension.zip')
  try {
    const written = writeZipFromDirectory(root, zipPath)
    const read = readZipEntryNames(zipPath)
    assert.deepEqual([...read].sort(), [...written.entryNames].sort())
    assert.ok(read.includes('src/background/main.js'))
    assert.ok(!read.some((n) => n.includes('\\')))
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zipDir, { recursive: true, force: true })
  }
})

test('readZipEntryNames 能识别出反斜杠条目（守住 #312 的缺陷形态）', () => {
  // 手工构造一个"PowerShell Compress-Archive 风格"的归档：条目名里带 `\`。
  const dir = mkdtempSync(join(tmpdir(), 'pim-badzip-'))
  const zipPath = join(dir, 'bad.zip')
  try {
    const name = Buffer.from('src\\background\\main.js', 'utf8')
    const data = Buffer.from('x', 'utf8')
    const crc = crc32(data)

    const local = Buffer.alloc(30)
    local.writeUInt32LE(0x04034b50, 0)
    local.writeUInt16LE(20, 4)
    local.writeUInt16LE(0, 6)
    local.writeUInt16LE(0, 8) // store
    local.writeUInt32LE(crc, 14)
    local.writeUInt32LE(data.length, 18)
    local.writeUInt32LE(data.length, 22)
    local.writeUInt16LE(name.length, 26)

    const central = Buffer.alloc(46)
    central.writeUInt32LE(0x02014b50, 0)
    central.writeUInt16LE(20, 4)
    central.writeUInt16LE(20, 6)
    central.writeUInt16LE(0, 8)
    central.writeUInt16LE(0, 10)
    central.writeUInt32LE(crc, 16)
    central.writeUInt32LE(data.length, 20)
    central.writeUInt32LE(data.length, 24)
    central.writeUInt16LE(name.length, 28)
    central.writeUInt32LE(0, 42)

    const centralStart = local.length + name.length + data.length
    const eocd = Buffer.alloc(22)
    eocd.writeUInt32LE(0x06054b50, 0)
    eocd.writeUInt16LE(1, 8)
    eocd.writeUInt16LE(1, 10)
    eocd.writeUInt32LE(central.length + name.length, 12)
    eocd.writeUInt32LE(centralStart, 16)

    writeFileSync(zipPath, Buffer.concat([local, name, data, central, name, eocd]))

    const names = readZipEntryNames(zipPath)
    assert.deepEqual(names, ['src\\background\\main.js'])
    // 这正是校验步骤要拦下的形态。
    assert.ok(names.some((n) => n.includes('\\')))
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
