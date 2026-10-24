import { createContext, useContext, useEffect, useMemo, useRef } from 'react';
import { Tree, type NodeApi, type TreeApi } from 'react-arborist';
import { ChevronDown, ChevronRight, FileText, Folder, FolderOpen, Loader2 } from 'lucide-react';
import type { FileItem } from '../../types';
import { breadcrumbSegments } from './fileBrowserState';

/** 目录数据加载状态（用于 AC-4.2：加载中/失败时另一区域不得显示成「空目录」）。 */
export type FolderLoadState = 'loading' | 'loaded' | 'error';

/** 传入树数据前必须带上 itemType：树据此区分「可展开的目录」与「叶子文件」。 */

export interface OneDriveFileTreeProps {
  /**
   * 目录路径 → 该目录的子项（目录与文件，与中栏列表同源同序）。
   * 目录节点可展开/进入，文件节点用于选中预览（REQ-4 / REQ-5）。
   */
  foldersByPath: Record<string, FileItem[]>;
  /** 每个目录的加载状态（可选）。 */
  folderStates?: Record<string, FolderLoadState>;
  /** 当前所在目录：树自动展开到该层并高亮（AC-5.2）。 */
  currentPath: string;
  /** 单击目录 = 展开/收起，**不切换中间列表**（AC-5.1）。 */
  onToggleFolder: (path: string) => void;
  /** 双击目录 = 进入（桌面）；手机抽屉由上层把单击也接到这里（AC-5.1 / AC-5.3）。 */
  onEnterFolder: (path: string) => void;
  /** 选中文件（仅更新预览面板，不改变当前目录，AC-5.1）。 */
  onSelectFile?: (item: FileItem) => void;
  /** 目录加载失败后的重试入口。 */
  onRetryFolder?: (path: string) => void;
}

interface TreeDataNode {
  id: string;
  name: string;
  path: string;
  itemType: string;
  mimeType: string | null;
  children: TreeDataNode[];
}

interface TreeContextValue {
  currentPath: string;
  states: Record<string, FolderLoadState>;
  onRetryFolder?: (path: string) => void;
  onEnterFolder: (path: string) => void;
}

const TreeContext = createContext<TreeContextValue>({
  currentPath: '/',
  states: {},
  onEnterFolder: () => {},
});

/**
 * 左栏文件树（react-arborist 虚拟化）。
 *
 * 导航模型按需求方实点确认的小样实现（决策 D-13 / D-27）：
 * **单击 = 展开/收起（不动中间列表）、双击 = 进入**；当前目录变化时由本组件自动展开到该层并高亮，
 * 因此不会出现旧实现里「点树没反应 / 树闪一下变展开 / 中间跳回第一个文件夹」三种乱象（AC-5.4）。
 *
 * 数据只取目录（`type=folder`）：中栏已经分页承载全部条目，树若也镜像整份内容，
 * 在大目录（工单实证单目录 8465 项）上既慢又会与分页列表的口径打架（REQ-4）。
 */
export default function OneDriveFileTree({
  foldersByPath,
  folderStates = {},
  currentPath,
  onToggleFolder,
  onEnterFolder,
  onSelectFile,
  onRetryFolder,
}: OneDriveFileTreeProps) {
  const treeRef = useRef<TreeApi<TreeDataNode> | null>(null);

  const { data, pathToId } = useMemo(() => {
    const ids = new Map<string, string>();
    const build = (path: string, depth: number): TreeDataNode[] => {
      const children = foldersByPath[path] ?? [];
      return children.map(child => {
        if (!ids.has(child.path)) ids.set(child.path, child.id);
        const isFolder = child.itemType === 'folder';
        const loaded = Object.prototype.hasOwnProperty.call(foldersByPath, child.path);
        return {
          id: child.id,
          name: child.name,
          path: child.path,
          itemType: child.itemType,
          mimeType: child.mimeType,
          // 只有目录会有子项；文件恒为叶子
          children: isFolder && loaded && depth < 24 ? build(child.path, depth + 1) : [],
        };
      });
    };
    return { data: build('/', 0), pathToId: ids };
  }, [foldersByPath]);

  // 当前目录变化 → 自动展开到该层（含自身），并把高亮交给 selection（受控）
  useEffect(() => {
    const api = treeRef.current;
    if (!api) return;
    for (const segment of breadcrumbSegments(currentPath)) {
      const id = pathToId.get(segment.path);
      if (id) api.open(id);
    }
  }, [currentPath, pathToId]);

  const context = useMemo(
    () => ({ currentPath, states: folderStates, onRetryFolder, onEnterFolder }),
    [currentPath, folderStates, onRetryFolder, onEnterFolder],
  );

  return (
    <TreeContext.Provider value={context}>
      <div className="h-full" data-testid="onedrive-tree">
        <Tree<TreeDataNode>
          ref={treeRef}
          data={data}
          width="100%"
          height={960}
          rowHeight={30}
          indent={14}
          openByDefault={false}
          disableDrag
          disableDrop
          disableEdit
          disableMultiSelection
          onSelect={nodes => {
            const node = nodes[0];
            if (!node) return;
            if (node.data.itemType !== 'folder') {
              // 文件：只更新预览面板，不改变当前目录（AC-5.1）
              onSelectFile?.(node.data as unknown as FileItem);
              return;
            }
            const api = treeRef.current;
            // 单击 = 展开/收起：旧实现只切中间菜单、不切展开态，用户感知为「点了没反应」
            if (node.isOpen) api?.close(node.id);
            else api?.open(node.id);
            onToggleFolder(node.data.path);
          }}
          onToggle={id => {
            const node = treeRef.current?.get(id);
            if (node) onToggleFolder(node.data.path);
          }}
        >
          {NodeRenderer}
        </Tree>
      </div>
    </TreeContext.Provider>
  );
}

function NodeRenderer({ node, style }: { node: NodeApi<TreeDataNode>; style: React.CSSProperties }) {
  const { currentPath, onRetryFolder, states, onEnterFolder } = useContext(TreeContext);
  const path = node.data.path;
  const isFolder = node.data.itemType === 'folder';
  const state = isFolder ? states[path] : undefined;
  // 高亮跟随「当前目录」而不是 arborist 的选择态：单击只负责展开/收起，不能把高亮带走
  const isSelected = isFolder && path === currentPath;

  return (
    <div
      style={style}
      data-tree-row={path}
      aria-selected={isSelected}
      // 双击进入。不用 arborist 的 onActivate：单击已经会切换展开态并触发重渲染，
      // 依赖它自己的双击判定在真实浏览器里不稳定（实测首次点击后 dblclick 不再到达）。
      onDoubleClick={event => {
        event.stopPropagation();
        if (isFolder) onEnterFolder(path);
      }}
      className={`flex h-full cursor-pointer items-center gap-1.5 pr-2 text-[13px] ${
        isSelected ? 'bg-[var(--pim-primary-soft)] text-[var(--pim-primary)]' : 'hover:bg-[var(--pim-surface-muted)]'
      }`}
    >
      <span className="flex h-4 w-4 shrink-0 items-center justify-center text-[var(--pim-text-muted)]">
        {isFolder ? node.isOpen ? <ChevronDown size={13} /> : <ChevronRight size={13} /> : null}
      </span>
      <span className="flex h-4 w-4 shrink-0 items-center justify-center text-[var(--pim-text-muted)]">
        {isFolder ? node.isOpen ? <FolderOpen size={14} /> : <Folder size={14} /> : <FileText size={14} />}
      </span>
      <span className="truncate" title={path}>
        {node.data.name}
      </span>

      {state === 'loading' && (
        <span className="ml-1 flex items-center text-[var(--pim-text-muted)]" data-testid={`tree-loading-${path}`}>
          <Loader2 size={12} className="animate-spin" />
        </span>
      )}

      {state === 'error' && (
        <span className="ml-1 flex items-center gap-1 text-[11px] text-[var(--pim-danger)]" data-testid={`tree-error-${path}`}>
          加载失败
          <button
            type="button"
            className="rounded border border-[var(--pim-border)] px-1 text-[10px] text-[var(--pim-primary)]"
            onClick={event => {
              event.stopPropagation();
              onRetryFolder?.(path);
            }}
          >
            重试
          </button>
        </span>
      )}
    </div>
  );
}
