import { useEffect, type HTMLAttributes } from 'react';
import { EditorProvider, useCurrentEditor } from '@tiptap/react';
import StarterKit from '@tiptap/starter-kit';
import Link from '@tiptap/extension-link';
import Underline from '@tiptap/extension-underline';
import {
  Bold,
  Italic,
  Underline as UnderlineIcon,
  List,
  ListOrdered,
  Quote,
  Code,
  Link2,
  Undo2,
  Redo2,
} from 'lucide-react';
import { sanitizeDescriptionHtml } from '../../utils/safeHtml';

interface RichDescriptionEditorProps {
  value: string;
  onChange: (html: string) => void;
  disabled?: boolean;
}

const RICH_DESCRIPTION_EXTENSIONS = [
  StarterKit.configure({ heading: { levels: [2, 3] }, link: false, underline: false }),
  Link.configure({ openOnClick: false, HTMLAttributes: { rel: 'noopener noreferrer', target: '_blank' } }),
  Underline,
];

const RICH_DESCRIPTION_EDITOR_PROPS = {
  attributes: {
    'aria-label': '描述',
  },
};

const TOOLBAR_BUTTON_CLASS = 'event-rich-editor-toolbar-button';

const EDITOR_CONTAINER_PROPS = {
  className: 'event-rich-editor-content',
  'data-description-html-preview': '',
} as HTMLAttributes<HTMLDivElement>;

function RichEditorToolbar() {
  const { editor } = useCurrentEditor();
  if (!editor) return null;
  const notEditable = !editor.isEditable;

  const setLinkUrl = () => {
    const previous = (editor.getAttributes('link').href as string | undefined) ?? '';
    const input = window.prompt('输入链接地址（http/https/mailto）', previous);
    if (input === null) return;
    const href = input.trim();
    if (href === '') {
      editor.chain().focus().extendMarkRange('link').unsetLink().run();
      return;
    }
    editor.chain().focus().extendMarkRange('link').setLink({ href }).run();
  };

  return (
    <div className="event-rich-editor-toolbar" role="toolbar" aria-label="描述格式工具栏">
      <button type="button" aria-label="加粗" title="加粗" aria-pressed={editor.isActive('bold')} disabled={notEditable} onClick={() => editor.chain().focus().toggleBold().run()} className={TOOLBAR_BUTTON_CLASS}>
        <Bold size={16} />
      </button>
      <button type="button" aria-label="斜体" title="斜体" aria-pressed={editor.isActive('italic')} disabled={notEditable} onClick={() => editor.chain().focus().toggleItalic().run()} className={TOOLBAR_BUTTON_CLASS}>
        <Italic size={16} />
      </button>
      <button type="button" aria-label="下划线" title="下划线" aria-pressed={editor.isActive('underline')} disabled={notEditable} onClick={() => editor.chain().focus().toggleUnderline().run()} className={TOOLBAR_BUTTON_CLASS}>
        <UnderlineIcon size={16} />
      </button>
      <button type="button" aria-label="无序列表" title="无序列表" aria-pressed={editor.isActive('bulletList')} disabled={notEditable} onClick={() => editor.chain().focus().toggleBulletList().run()} className={TOOLBAR_BUTTON_CLASS}>
        <List size={16} />
      </button>
      <button type="button" aria-label="有序列表" title="有序列表" aria-pressed={editor.isActive('orderedList')} disabled={notEditable} onClick={() => editor.chain().focus().toggleOrderedList().run()} className={TOOLBAR_BUTTON_CLASS}>
        <ListOrdered size={16} />
      </button>
      <button type="button" aria-label="引用" title="引用" aria-pressed={editor.isActive('blockquote')} disabled={notEditable} onClick={() => editor.chain().focus().toggleBlockquote().run()} className={TOOLBAR_BUTTON_CLASS}>
        <Quote size={16} />
      </button>
      <button type="button" aria-label="代码块" title="代码块" aria-pressed={editor.isActive('codeBlock')} disabled={notEditable} onClick={() => editor.chain().focus().toggleCodeBlock().run()} className={TOOLBAR_BUTTON_CLASS}>
        <Code size={16} />
      </button>
      <button type="button" aria-label="链接" title="链接" aria-pressed={editor.isActive('link')} disabled={notEditable} onClick={setLinkUrl} className={TOOLBAR_BUTTON_CLASS}>
        <Link2 size={16} />
      </button>
      <button type="button" aria-label="撤销" title="撤销" disabled={notEditable} onClick={() => editor.chain().focus().undo().run()} className={TOOLBAR_BUTTON_CLASS}>
        <Undo2 size={16} />
      </button>
      <button type="button" aria-label="重做" title="重做" disabled={notEditable} onClick={() => editor.chain().focus().redo().run()} className={TOOLBAR_BUTTON_CLASS}>
        <Redo2 size={16} />
      </button>
    </div>
  );
}

function RichEditorEditableSync({ disabled }: { disabled: boolean }) {
  const { editor } = useCurrentEditor();
  useEffect(() => {
    if (!editor) return;
    if (editor.isEditable === !disabled) return;
    editor.setEditable(!disabled);
  }, [editor, disabled]);
  return null;
}

export default function RichDescriptionEditor({ value, onChange, disabled = false }: RichDescriptionEditorProps) {
  return (
    <div className="event-rich-editor">
      <EditorProvider
        extensions={RICH_DESCRIPTION_EXTENSIONS}
        content={value}
        immediatelyRender={false}
        editable={!disabled}
        editorProps={RICH_DESCRIPTION_EDITOR_PROPS}
        onUpdate={({ editor: ed }) => {
          onChange(sanitizeDescriptionHtml(ed.getHTML()));
        }}
        slotBefore={!disabled ? <RichEditorToolbar /> : undefined}
        editorContainerProps={EDITOR_CONTAINER_PROPS}
      >
        <RichEditorEditableSync disabled={disabled} />
      </EditorProvider>
    </div>
  );
}
