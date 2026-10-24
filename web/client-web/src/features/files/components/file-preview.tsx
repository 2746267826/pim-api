import { useEffect, useState } from 'react'
import { Download, ExternalLink, RotateCcw, Save, X } from 'lucide-react'
import { fetchBlob } from '@/api/client'
import { filesApi } from '../api'
import { useFileMutations } from '../queries'
import type { FileItem, FileTextSnapshot } from '../types'
import { formatBytes } from '../upload-chunk-plan'
import { Button, Card, CardTitle, EmptyState, Spinner, StatusBadge, Textarea } from '@/components/ui'
import { notifyError, notifySuccess } from '@/lib/notify'
import { formatTime } from '@/lib/datetime'
import { cn } from '@/lib/utils'

/** 文件预览面板：图片（blob）/ 可编辑文本（含快照）/ Office 在线预览 */
export function FilePreview({ item, onClose }: { item: FileItem; onClose: () => void }) {
  const isImage = item.mimeType?.startsWith('image/')
  const isText = isTextLike(item)
  const [imageUrl, setImageUrl] = useState<string | null>(null)
  const [text, setText] = useState<string | null>(null)
  const [original, setOriginal] = useState('')
  const [snapshots, setSnapshots] = useState<FileTextSnapshot[]>([])
  const [previewUrl, setPreviewUrl] = useState<string | null>(null)
  const mutations = useFileMutations()

  useEffect(() => {
    setImageUrl(null)
    setText(null)
    setPreviewUrl(null)
    setSnapshots([])
    let revoked: string | null = null

    if (isImage) {
      void fetchBlob(`/api/v1/files/items/${item.id}/content`)
        .then((blob) => {
          const u = URL.createObjectURL(blob)
          revoked = u
          setImageUrl(u)
        })
        .catch(() => notifyError('图片加载失败'))
    } else if (isText) {
      void filesApi
        .readText(item.id)
        .then((res) => {
          setText(res.content)
          setOriginal(res.content)
        })
        .catch(() => setText(''))
      void filesApi.snapshots(item.id).then(setSnapshots).catch(() => {})
    } else {
      void filesApi.previewUrl(item.id).then((r) => setPreviewUrl(r.url)).catch(() => {})
    }
    return () => {
      if (revoked) URL.revokeObjectURL(revoked)
    }
  }, [item.id, isImage, isText])

  const dirty = text != null && text !== original

  return (
    <Card className="flex h-full min-h-0 flex-col">
      {/* 头 */}
      <div className="flex shrink-0 items-start gap-2 border-b border-divider px-3 py-2.5">
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] font-semibold text-text-1" title={item.name}>{item.name}</div>
          <div className="mt-0.5 flex items-center gap-2 text-[11px] text-text-4">
            <span className="tnum">{formatBytes(item.size)}</span>
            <span>{item.mimeType ?? '未知类型'}</span>
            {dirty && <StatusBadge tone="warn" dot={false}>未保存</StatusBadge>}
          </div>
        </div>
        <button type="button" aria-label="关闭预览" onClick={onClose} className="rounded-ctl p-1 text-text-3 outline-none hover:bg-surface">
          <X className="size-4" aria-hidden />
        </button>
      </div>

      {/* 体 */}
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        {isImage ? (
          imageUrl ? (
            <img src={imageUrl} alt={item.name} className="max-w-full rounded-ctl" />
          ) : (
            <div className="grid h-40 place-items-center"><Spinner /></div>
          )
        ) : isText ? (
          text == null ? (
            <div className="grid h-40 place-items-center"><Spinner /></div>
          ) : (
            <Textarea
              value={text}
              onChange={(e) => setText(e.target.value)}
              className="mono min-h-[320px] resize-y text-xs leading-5"
              spellCheck={false}
            />
          )
        ) : previewUrl ? (
          <div className="space-y-2">
            <iframe src={previewUrl} title={item.name} className="h-[380px] w-full rounded-ctl border border-border" />
            <p className="text-[11px] text-text-4">由 Microsoft Office 在线预览（服务器只提供链接，不中转字节）</p>
          </div>
        ) : (
          <EmptyState size="sm" title="该类型暂不支持在线预览" description="可下载后在本地打开，或在 OneDrive 中查看。" />
        )}

        {/* 文本快照 */}
        {isText && snapshots.length > 0 && (
          <div className="mt-3">
            <CardTitle className="text-[13px]">编辑快照（{snapshots.length}）</CardTitle>
            <div className="mt-1.5 space-y-1">
              {snapshots.slice(0, 10).map((s) => (
                <div key={s.id} className={cn('flex items-center gap-2 rounded-ctl border border-border px-2.5 py-1.5 text-[12px]')}>
                  <StatusBadge tone={s.reason === 'pre-edit' ? 'info' : 'neutral'} dot={false}>
                    {s.reason === 'pre-edit' ? '编辑前' : '恢复前'}
                  </StatusBadge>
                  <span className="tnum text-text-3">{formatTime(s.createdAt)}</span>
                  <span className="tnum text-text-4">{formatBytes(s.byteSize)}</span>
                  <button
                    type="button"
                    className="ml-auto inline-flex items-center gap-1 text-primary outline-none hover:underline"
                    onClick={async () => {
                      try {
                        await mutations.restoreSnapshot.mutateAsync({ id: item.id, snapshotId: s.id })
                        const res = await filesApi.readText(item.id)
                        setText(res.content)
                        setOriginal(res.content)
                        setSnapshots(await filesApi.snapshots(item.id))
                        notifySuccess('已恢复到该快照')
                      } catch (err) {
                        notifyError(err instanceof Error ? err.message : '恢复失败')
                      }
                    }}
                  >
                    <RotateCcw className="size-3" aria-hidden /> 恢复
                  </button>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>

      {/* 底：操作 */}
      <div className="flex shrink-0 items-center gap-1.5 border-t border-divider px-3 py-2.5">
        <Button
          variant="ghost"
          size="sm"
          onClick={async () => {
            const { url } = await filesApi.downloadUrl(item.id)
            window.open(url, '_blank', 'noopener')
          }}
        >
          <Download className="size-3.5" aria-hidden /> 下载
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={async () => {
            const { url } = await filesApi.openLink(item.id)
            window.open(url, '_blank', 'noopener')
          }}
        >
          <ExternalLink className="size-3.5" aria-hidden /> OneDrive
        </Button>
        {isText && (
          <Button
            variant="primary"
            size="sm"
            className="ml-auto"
            disabled={!dirty}
            loading={mutations.saveText.isPending}
            onClick={async () => {
              try {
                await mutations.saveText.mutateAsync({ id: item.id, content: text ?? '' })
                setOriginal(text ?? '')
                setSnapshots(await filesApi.snapshots(item.id))
                notifySuccess('已保存（保存前内容已留快照）')
              } catch (err) {
                notifyError(err instanceof Error ? err.message : '保存失败')
              }
            }}
          >
            <Save className="size-3.5" aria-hidden /> 保存
          </Button>
        )}
      </div>
    </Card>
  )
}

/** 文本类判定（与后端 OneDriveContentService 口径一致） */
function isTextLike(item: FileItem): boolean {
  const mime = item.mimeType ?? ''
  if (mime.startsWith('text/') || /json|xml|yaml/.test(mime)) return true
  return /\.(txt|md|markdown|json|csv|log|ya?ml|xml)$/i.test(item.name)
}
