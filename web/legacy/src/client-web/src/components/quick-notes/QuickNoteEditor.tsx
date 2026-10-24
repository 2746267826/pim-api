import { useEffect, useMemo, useRef } from 'react';
import {
  BlockTypeSelect,
  BoldItalicUnderlineToggles,
  CreateLink,
  headingsPlugin,
  imagePlugin,
  InsertImage,
  InsertThematicBreak,
  linkPlugin,
  listsPlugin,
  ListsToggle,
  markdownShortcutPlugin,
  MDXEditor,
  type MDXEditorMethods,
  quotePlugin,
  Separator,
  thematicBreakPlugin,
  toolbarPlugin,
  UndoRedo,
} from '@mdxeditor/editor';
import '@mdxeditor/editor/style.css';

import { downloadQuickNoteAttachmentBlob, uploadQuickNoteAttachment } from '../../api/quickNotes';
import { getQuickNoteAttachmentIdFromDownloadUrl } from './quickNoteAttachmentBlobUrls';

export interface QuickNoteEditorProps {
  value: string;
  onChange?: (value: string) => void;
  minHeight?: number;
  readOnly?: boolean;
}

export default function QuickNoteEditor({
  value,
  onChange,
  minHeight = 220,
  readOnly = false,
}: QuickNoteEditorProps) {
  const editorRef = useRef<MDXEditorMethods>(null);
  const previewObjectUrls = useRef<Map<string, string>>(new Map());

  useEffect(() => {
    const editor = editorRef.current;
    if (editor && editor.getMarkdown() !== value) {
      editor.setMarkdown(value);
    }
  }, [value]);

  useEffect(() => () => {
    previewObjectUrls.current.forEach(url => URL.revokeObjectURL(url));
    previewObjectUrls.current.clear();
  }, []);

  const plugins = useMemo(() => {
    const sharedPlugins = [
      headingsPlugin(),
      listsPlugin(),
      quotePlugin(),
      thematicBreakPlugin(),
      linkPlugin(),
      imagePlugin({
        imageUploadHandler: async file => {
          const uploaded = await uploadQuickNoteAttachment(file);
          return uploaded.downloadUrl;
        },
        imagePreviewHandler: async imageSource => {
          const attachmentId = getQuickNoteAttachmentIdFromDownloadUrl(imageSource);
          if (!attachmentId) {
            return imageSource;
          }

          const cachedObjectUrl = previewObjectUrls.current.get(attachmentId);
          if (cachedObjectUrl) {
            return cachedObjectUrl;
          }

          const blob = await downloadQuickNoteAttachmentBlob(attachmentId);
          const objectUrl = URL.createObjectURL(blob);
          previewObjectUrls.current.set(attachmentId, objectUrl);
          return objectUrl;
        },
      }),
      markdownShortcutPlugin(),
    ];

    if (readOnly) {
      return sharedPlugins;
    }

    return [
      ...sharedPlugins,
      toolbarPlugin({
        toolbarContents: () => (
          <>
            <UndoRedo />
            <Separator />
            <BlockTypeSelect />
            <BoldItalicUnderlineToggles />
            <ListsToggle />
            <Separator />
            <CreateLink />
            <InsertImage />
            <InsertThematicBreak />
          </>
        ),
        toolbarClassName: 'quick-note-editor-toolbar',
      }),
    ];
  }, [readOnly]);

  return (
    <div
      className={`quick-note-editor overflow-hidden rounded-lg border border-slate-200 bg-white text-sm text-slate-800 focus-within:border-blue-300 focus-within:ring-2 focus-within:ring-blue-100 ${
        readOnly ? 'quick-note-editor-readonly border-transparent bg-transparent' : ''
      }`}
      style={{ minHeight }}
    >
      <MDXEditor
        ref={editorRef}
        markdown={value}
        onChange={nextValue => onChange?.(nextValue)}
        plugins={plugins}
        readOnly={readOnly}
        contentEditableClassName="quick-note-editor-content min-h-[inherit] px-3 py-2 leading-6 outline-none"
        className="h-full"
      />
    </div>
  );
}
