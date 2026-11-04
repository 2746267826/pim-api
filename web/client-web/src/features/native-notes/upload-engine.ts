import type { components } from '@/api/generated'
import type { MaterialBundle, MaterialFileSpec, NoteMaterialUpload, NoteMaterialVersion } from './material-model'
import { sha256Hex } from './material-model'

export interface NoteMaterialUploadApi {
  begin(
    body: components['schemas']['BeginNoteMaterialUploadRequest'],
    signal?: AbortSignal,
  ): Promise<NoteMaterialUpload>
  uploadPart(
    versionId: string,
    fileIndex: number,
    partIndex: number,
    body: Blob,
    sha256: string,
    signal?: AbortSignal,
  ): Promise<unknown>
  complete(versionId: string, signal?: AbortSignal): Promise<NoteMaterialUpload>
  clone?(sourceVersionId: string, body: components['schemas']['CloneNoteMaterialVersionRequest'], signal?: AbortSignal): Promise<NoteMaterialUpload>
  version?(versionId: string, signal?: AbortSignal): Promise<NoteMaterialVersion>
}

export interface MaterialUploadProgress {
  completedParts: number
  totalParts: number
  currentPath: string
}

export interface MaterialUploadOptions {
  /** Explicit confirmation to persist the immutable version on this server. */
  storageConsent: boolean
  signal?: AbortSignal
  assertActive?: () => void
  cloneSourceVersionId?: string
  expectedCurrentVersionId?: string | null
}

export const MAX_UPLOAD_PART_SIZE_BYTES = 64 * 1024 * 1024

export async function uploadMaterialBundle(
  bundle: MaterialBundle,
  api: NoteMaterialUploadApi,
  options: MaterialUploadOptions,
  onProgress?: (progress: MaterialUploadProgress) => void,
): Promise<NoteMaterialUpload> {
  assertUploadActive(options)
  if (!options.storageConsent) throw new Error('请先确认将材料保存到当前服务器')
  const beginRequest = {
    versionId: bundle.versionId,
    noteId: bundle.noteId,
    baseVersionId: bundle.baseVersionId,
    inputJson: bundle.inputJson,
    manifestJson: bundle.manifestJson,
    storageConsent: options.storageConsent,
  }
  let began: NoteMaterialUpload
  if (options.cloneSourceVersionId) {
    if (options.cloneSourceVersionId !== bundle.baseVersionId) {
      throw new Error('续写来源与固定材料基版本不一致')
    }
    try {
      began = await requireClone(api)(options.cloneSourceVersionId, {
        expectedCurrentVersionId: options.expectedCurrentVersionId,
        material: beginRequest,
      }, options.signal)
    } catch (failure) {
      assertUploadActive(options)
      const observed = await recoverIntent(bundle, api, options, failure)
      began = observed.upload
    }
  } else {
    began = await api.begin(beginRequest, options.signal)
  }
  assertUploadActive(options)
  if (began.noteId !== bundle.noteId || began.versionId !== bundle.versionId) {
    throw new Error('服务器返回的材料版本与提交意图不一致')
  }
  if (began.baseVersionId !== bundle.baseVersionId) {
    throw new Error('服务器返回的材料来源版本与提交意图不一致')
  }
  if (began.files.length !== bundle.files.length) {
    throw new Error('服务器返回的材料文件数量与本地固定清单不一致')
  }

  const partSize = began.limits?.partSizeBytes
  if (!Number.isInteger(partSize) || partSize <= 0 || partSize > MAX_UPLOAD_PART_SIZE_BYTES) {
    throw new Error('服务器返回的材料分块大小无效')
  }
  const totalParts = bundle.files.reduce((total, file) =>
    total + Math.ceil(file.sizeBytes / partSize), 0)
  if (!Number.isSafeInteger(totalParts) || totalParts < 0) {
    throw new Error('服务器返回的材料分块总数无效')
  }
  if (began.phase === 'Uploaded' || began.phase === 'Conflict') {
    assertCompleteReceipt(bundle.files, began, partSize)
    return began
  }
  let completedParts = 0

  for (let fileIndex = 0; fileIndex < bundle.files.length; fileIndex++) {
    assertUploadActive(options)
    const file = bundle.files[fileIndex]!
    const remote = began.files[fileIndex]
    const partCount = Math.ceil(file.sizeBytes / partSize)
    assertSameFile(file, remote, fileIndex, partCount)
    assertUploadedParts(remote.uploadedParts, partCount, file.path)
    if (file.preserved) {
      if (remote.uploadedParts.length !== partCount) {
        throw new Error(`服务器未确认完整复用原材料：${file.path}`)
      }
      completedParts += partCount
      continue
    }
    const uploaded = new Set(remote.uploadedParts)
    for (let partIndex = 0; partIndex < partCount; partIndex++) {
      assertUploadActive(options)
      if (uploaded.has(partIndex)) {
        completedParts++
        onProgress?.({ completedParts, totalParts, currentPath: file.path })
        continue
      }
      const start = partIndex * partSize
      const end = Math.min(file.sizeBytes, start + partSize)
      const bytes = file.bytes.slice(start, end)
      if (bytes.length !== end - start) throw new Error(`材料分块读取不完整：${file.path}`)
      const partHash = await sha256Hex(bytes)
      assertUploadActive(options)
      await api.uploadPart(
        bundle.versionId,
        fileIndex,
        partIndex,
        new Blob([bytes], { type: 'application/octet-stream' }),
        partHash,
        options.signal,
      )
      assertUploadActive(options)
      completedParts++
      onProgress?.({ completedParts, totalParts, currentPath: file.path })
    }
  }

  assertUploadActive(options)
  let complete: NoteMaterialUpload
  try {
    complete = await api.complete(bundle.versionId, options.signal)
  } catch (failure) {
    assertUploadActive(options)
    complete = await recoverCompletedIntent(bundle, api, options, failure)
  }
  assertUploadActive(options)
  if (complete.versionId !== bundle.versionId || complete.noteId !== bundle.noteId) {
    throw new Error('服务器完成回执与本次材料版本不一致')
  }
  if (complete.baseVersionId !== bundle.baseVersionId) {
    throw new Error('服务器完成回执与本次来源版本不一致')
  }
  if (complete.phase !== 'Uploaded' && complete.phase !== 'Conflict') {
    throw new Error('服务器尚未确认全部材料分块完整')
  }
  assertCompleteReceipt(bundle.files, complete, partSize)
  return complete
}

function requireClone(api: NoteMaterialUploadApi) {
  if (!api.clone) throw new Error('服务器已提供续写能力，但当前 API 客户端缺少 clone 支持')
  return api.clone.bind(api)
}

async function recoverCompletedIntent(
  bundle: MaterialBundle,
  api: NoteMaterialUploadApi,
  options: MaterialUploadOptions,
  originalFailure: unknown,
): Promise<NoteMaterialUpload> {
  const observed = await recoverIntent(bundle, api, options, originalFailure)
  if (observed.upload.phase !== 'Uploaded' && observed.upload.phase !== 'Conflict') throw originalFailure
  return observed.upload
}

async function recoverIntent(
  bundle: MaterialBundle,
  api: NoteMaterialUploadApi,
  options: MaterialUploadOptions,
  originalFailure: unknown,
): Promise<NoteMaterialVersion> {
  if (!api.version) throw originalFailure
  try {
    const observed = await api.version(bundle.versionId, options.signal)
    assertUploadActive(options)
    if (observed.upload.versionId !== bundle.versionId || observed.upload.noteId !== bundle.noteId ||
        observed.upload.baseVersionId !== bundle.baseVersionId ||
        observed.inputJson !== bundle.inputJson || observed.manifestJson !== bundle.manifestJson) {
      throw new Error('同一版本尚未确认或服务端固定输入与本次提交不同')
    }
    if (!['Uploading', 'Uploaded', 'Conflict'].includes(observed.upload.phase)) {
      throw new Error('同一版本处于不可恢复状态')
    }
    return observed
  } catch {
    assertUploadActive(options)
    throw originalFailure
  }
}

function assertCompleteReceipt(
  files: MaterialFileSpec[],
  receipt: NoteMaterialUpload,
  partSize: number,
): void {
  if (receipt.files.length !== files.length) {
    throw new Error('服务器完成回执的材料文件数量不一致')
  }
  files.forEach((file, index) => {
    const partCount = Math.ceil(file.sizeBytes / partSize)
    const remote = receipt.files[index]
    assertSameFile(file, remote, index, partCount)
    assertUploadedParts(remote.uploadedParts, partCount, file.path)
    if (remote.uploadedParts.length !== partCount) {
      throw new Error(`服务器完成回执未确认全部材料分块：${file.path}`)
    }
  })
}

function assertUploadActive(options: MaterialUploadOptions): void {
  if (options.signal?.aborted) {
    const error = new Error('材料上传所属会话已结束')
    error.name = 'AbortError'
    throw error
  }
  options.assertActive?.()
}

function assertSameFile(
  local: MaterialFileSpec,
  remote: components['schemas']['NoteMaterialUploadFileDto'] | undefined,
  fileIndex: number,
  partCount: number,
) {
  if (!remote) throw new Error(`服务器缺少材料文件序号 ${fileIndex}`)
  if (remote.index !== fileIndex || remote.path !== local.path || remote.kind !== local.kind ||
      remote.sha256 !== local.sha256 || remote.sizeBytes !== local.sizeBytes) {
    throw new Error(`服务器材料清单与本地固定文件不一致：${local.path}`)
  }
  if (!Number.isInteger(remote.partCount) || remote.partCount !== partCount) {
    throw new Error(`服务器返回的分块数与本地固定文件不一致：${local.path}`)
  }
}

function assertUploadedParts(parts: IReadOnlyNumberList, partCount: number, path: string): void {
  if (!Array.isArray(parts)) throw new Error(`服务器返回的已上传分块无效：${path}`)
  const seen = new Set<number>()
  for (const part of parts) {
    if (!Number.isInteger(part) || part < 0 || part >= partCount || seen.has(part)) {
      throw new Error(`服务器返回的已上传分块无效：${path}`)
    }
    seen.add(part)
  }
}

type IReadOnlyNumberList = readonly number[]
