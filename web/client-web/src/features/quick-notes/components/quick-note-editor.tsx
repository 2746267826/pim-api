import { EditorContent, useEditor } from '@tiptap/react'
import StarterKit from '@tiptap/starter-kit'
import Image from '@tiptap/extension-image'
import Link from '@tiptap/extension-link'
import { useEffect } from 'react'
import { fetchBlob } from '@/api/client'
import { quickNotesApi } from '../api'
import { cn } from '@/lib/utils'

async function quickNoteUpload(file: File) {
  return quickNotesApi.uploadAttachment(file)
}

/*
 * TipTap Markdown 编辑器（快速记录/闪念弹窗用）。
 * 图片粘贴/上传后以 blob URL 内嵌（附件下载端点带认证，img 直接引用会 401）。
 */
export function QuickNoteEditor({
  markdown,
  onChange,
  className,
}: {
  markdown: string
  onChange: (markdown: string) => void
  className?: string
}) {
  const editor = useEditor({
    extensions: [
      StarterKit,
      Image.configure({ inline: false }),
      Link.configure({ openOnClick: false }),
    ],
    content: markdown || '',
    onUpdate: ({ editor: ed }) => {
      onChange(ed.getHTML())
    },
    editorProps: {
      attributes: {
        class: cn(
          'prose-sm min-h-56 max-w-none outline-none',
          'text-sm leading-5 text-text-1',
          '[&_h1]:text-lg [&_h2]:text-base [&_h3]:text-sm [&_h1,h2,h3]:font-semibold [&_ul]:list-disc [&_ol]:list-decimal [&_ul,ol]:pl-5',
          '[&_pre]:rounded-ctl [&_pre]:bg-surface-2 [&_pre]:p-2 [&_code]:text-xs',
          '[&_img]:max-w-full [&_img]:rounded-ctl',
          '[&_a]:text-primary',
        ),
      },
      handlePaste: (view, event) => {
        const files = event.clipboardData?.files
        if (!files?.length) return false
        const image = files[0]
        if (!image.type.startsWith('image/')) return false
        void (async () => {
          try {
            const att = await quickNoteUpload(image)
            const blob = await fetchBlob(att.downloadUrl)
            const url = URL.createObjectURL(blob)
            view.dispatch(view.state.tr.replaceSelectionWith(view.state.schema.nodes.image.create({ src: url })))
          } catch {
            // 附件上传失败交给全局 toast
          }
        })()
        return true
      },
    },
  })

  useEffect(() => {
    if (editor && markdown && editor.getHTML() !== markdown && !editor.isFocused) {
      editor.commands.setContent(markdown)
    }
  }, [markdown, editor])

  return <EditorContent editor={editor} className={className} />
}
