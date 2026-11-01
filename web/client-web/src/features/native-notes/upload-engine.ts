import type { components } from '@/api/generated'
import type { MaterialBundle, MaterialFileSpec, NoteMaterialUpload } from './material-model'
import { sha256Hex } from './material-model'

export interface NoteMaterialUploadApi {
  begin(body: components['schemas']['BeginNoteMaterialUploadRequest']): Promise<NoteMaterialUpload>
  uploadPart(
    versionId: string,
    fileIndex: number,
    partIndex: number,
    body: Blob,
    sha256: string,
  ): Promise<unknown>
  complete(versionId: string): Promise<NoteMaterialUpload>
}

export interface MaterialUploadProgress {
  completedParts: number
  totalParts: number
  currentPath: string
}

export async function uploadMaterialBundle(
  bundle: MaterialBundle,
  api: NoteMaterialUploadApi,
  onProgress?: (progress: MaterialUploadProgress) => void,
): Promise<NoteMaterialUpload> {
  const began = await api.begin({
    versionId: bundle.versionId,
    noteId: bundle.noteId,
    baseVersionId: bundle.baseVersionId,
    inputJson: bundle.inputJson,
    manifestJson: bundle.manifestJson,
    storageConsent: true,
  })
  if (began.noteId !== bundle.noteId || began.versionId !== bundle.versionId) {
    throw new Error('服务器返回的材料版本与提交意图不一致')
  }
  if (began.files.length !== bundle.files.length) {
    throw new Error('服务器返回的材料文件数量与本地固定清单不一致')
  }

  const totalParts = bundle.files.reduce((total, file) =>
    total + Math.ceil(file.sizeBytes / began.limits.partSizeBytes), 0)
  let completedParts = 0

  for (let fileIndex = 0; fileIndex < bundle.files.length; fileIndex++) {
    const file = bundle.files[fileIndex]!
    const remote = began.files[fileIndex]
    const partCount = Math.ceil(file.sizeBytes / began.limits.partSizeBytes)
    assertSameFile(file, remote, fileIndex, partCount)
    const uploaded = new Set(remote.uploadedParts)
    for (let partIndex = 0; partIndex < partCount; partIndex++) {
      if (uploaded.has(partIndex)) {
        completedParts++
        onProgress?.({ completedParts, totalParts, currentPath: file.path })
        continue
      }
      const start = partIndex * began.limits.partSizeBytes
      const end = Math.min(file.sizeBytes, start + began.limits.partSizeBytes)
      const bytes = file.bytes.slice(start, end)
      if (bytes.length !== end - start) throw new Error(`材料分块读取不完整：${file.path}`)
      const partHash = await sha256Hex(bytes)
      await api.uploadPart(
        bundle.versionId,
        fileIndex,
        partIndex,
        new Blob([bytes], { type: 'application/octet-stream' }),
        partHash,
      )
      completedParts++
      onProgress?.({ completedParts, totalParts, currentPath: file.path })
    }
  }

  const complete = await api.complete(bundle.versionId)
  if (complete.versionId !== bundle.versionId || complete.noteId !== bundle.noteId) {
    throw new Error('服务器完成回执与本次材料版本不一致')
  }
  return complete
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
