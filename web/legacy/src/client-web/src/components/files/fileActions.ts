/**
 * 文件管理操作的前端规则（REQ-12 / REQ-15 / REQ-17 / REQ-18 / REQ-19 / REQ-20）。
 *
 * 抽成纯函数：这些规则直接对应验收条款（重名策略、非法名称、二次确认、
 * 批量逐项报告、菜单项差异、下载阈值），必须能被逐条断言，而不是埋在组件里靠人肉点。
 */
import type { FileItem } from '../../types';

/** P4：下载确认阈值 100MB。 */
export const DOWNLOAD_CONFIRM_THRESHOLD_BYTES = 100 * 1024 * 1024;

/** P5：单文件上传上限 2GB；超过提示改用 OneDrive 客户端。 */
export const UPLOAD_MAX_BYTES = 2 * 1024 * 1024 * 1024;

/**
 * OneDrive 侧与后端一致的非法字符（REQ-15 / AC-15.2）。
 * 前端先拦是为了即时反馈；真正的权威校验在后端（OneDriveNameValidator），
 * 两侧规则必须一致——不一致会出现「前端放行、后端报错」的坏体验。
 */
const INVALID_NAME_CHARACTERS = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

const RESERVED_NAMES = [
  '.lock', 'CON', 'PRN', 'AUX', 'NUL',
  'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
  'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9',
  '_vti_', 'desktop.ini',
];

/**
 * 校验名称，返回**可读**错误信息；合法时返回 null（AC-15.2：给可读错误并阻止提交）。
 */
export function validateEntryName(name: string | null | undefined): string | null {
  const trimmed = (name ?? '').trim();
  if (!trimmed) return '名称不能为空';
  if (trimmed.length > 255) return '名称过长（最多 255 个字符）';
  if (INVALID_NAME_CHARACTERS.some(char => trimmed.includes(char))) {
    return '名称不能包含下列任一字符：\\ / : * ? " < > |';
  }
  if (trimmed.endsWith('.') || trimmed.endsWith(' ')) return '名称不能以句点或空格结尾';

  const base = trimmed.includes('.') ? trimmed.slice(0, trimmed.lastIndexOf('.')) : trimmed;
  const upper = trimmed.toUpperCase();
  const upperBase = base.toUpperCase();
  if (RESERVED_NAMES.some(reserved => reserved.toUpperCase() === upper || reserved.toUpperCase() === upperBase)) {
    return `「${trimmed}」是 OneDrive 保留名称，请换一个`;
  }
  return null;
}

/**
 * 预测重名时的最终名称（REQ-12：绝不覆盖）。
 *
 * 格式按**真实账号实测**：Graph 的 `conflictBehavior=rename` 用的是
 * `名称 1.ext`（空格 + 序号，**没有括号**），此前按 `名称 (1).ext` 预测与实测不符。
 * 仅用于**提前告知用户**会发生什么；最终名称仍以服务端回读结果为准（Graph 才是权威）。
 */
export function predictRenamedName(name: string, existingNames: readonly string[]): string {
  const taken = new Set(existingNames.map(n => n.toLowerCase()));
  if (!taken.has(name.toLowerCase())) return name;

  const dot = name.lastIndexOf('.');
  const stem = dot > 0 ? name.slice(0, dot) : name;
  const extension = dot > 0 ? name.slice(dot) : '';

  for (let index = 1; index < 1000; index += 1) {
    const candidate = `${stem} ${index}${extension}`;
    if (!taken.has(candidate.toLowerCase())) return candidate;
  }
  return `${stem} ${Date.now()}${extension}`;
}

/** AC-20.2：超过阈值先确认（显示大小）。 */
export function requiresDownloadConfirmation(size: number | null | undefined): boolean {
  return typeof size === 'number' && size > DOWNLOAD_CONFIRM_THRESHOLD_BYTES;
}

/** 人类可读大小（确认弹窗与提示共用，避免两处格式不一致）。 */
export function formatBytes(size: number | null | undefined): string {
  if (size === null || size === undefined) return '—';
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  if (size < 1024 * 1024 * 1024) return `${(size / 1024 / 1024).toFixed(1)} MB`;
  return `${(size / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

export function exceedsUploadLimit(size: number): boolean {
  return size > UPLOAD_MAX_BYTES;
}

/** REQ-19 / AC-19.1：行内菜单项按条目类型区分——文件夹没有「下载」。 */
export type RowAction =
  | 'download'
  | 'share'
  | 'rename'
  | 'move'
  | 'delete'
  | 'open-in-onedrive'
  | 'open';

export function rowActionsFor(item: Pick<FileItem, 'itemType'>): RowAction[] {
  const shared: RowAction[] = ['rename', 'move', 'delete', 'open-in-onedrive'];
  return item.itemType === 'folder'
    // 文件夹：可进入、可分享（分享文件夹同样有效）、无「下载」
    ? ['open', 'share', ...shared]
    // 文件：可下载、可分享
    : ['download', 'share', ...shared];
}

/** AC-17.1：删除确认文案必须含还原指引（个人版无回收站 API，还原只能去 OneDrive 网页版）。 */
export function deleteConfirmationMessage(names: readonly string[]): string {
  const label = names.length === 1 ? `「${names[0]}」` : `选中的 ${names.length} 项`;
  return `${label}将从 OneDrive 删除。还原需前往 OneDrive 网页版的回收站，PIM 内无法还原。`;
}

export interface BatchOutcome<T> {
  succeeded: T[];
  failed: { item: T; reason: string }[];
}

/**
 * AC-18.1 / AC-17.2：批量操作必须**逐项报告**，失败项要带原因。
 * 这里只做结果归类，真正的执行由调用方逐项进行（绝不并发到无法归因）。
 */
export function summarizeBatchResults<T>(
  results: readonly { item: T; error?: string | null }[],
): BatchOutcome<T> {
  const succeeded: T[] = [];
  const failed: { item: T; reason: string }[] = [];
  for (const result of results) {
    if (result.error) {
      failed.push({ item: result.item, reason: result.error });
    } else {
      succeeded.push(result.item);
    }
  }
  return { succeeded, failed };
}

/** 批量结果的用户可读摘要（部分失败必须列出失败项）。 */
export function describeBatchOutcome(outcome: BatchOutcome<FileItem>, action: string): string {
  if (outcome.failed.length === 0) {
    return `${action}完成：${outcome.succeeded.length} 项`;
  }
  if (outcome.succeeded.length === 0) {
    return `${action}失败：${outcome.failed.length} 项（${outcome.failed.map(f => f.item.name).join('、')}）`;
  }
  return `${action}部分完成：成功 ${outcome.succeeded.length} 项，失败 ${outcome.failed.length} 项（${outcome.failed
    .map(f => `${f.item.name}：${f.reason}`)
    .join('；')}）`;
}

/**
 * AC-18.2：批量操作不得误伤未勾选项——选择集必须严格等于勾选项。
 * 同时过滤掉已不存在的项（例如在别处被删），避免对空气发请求。
 */
export function resolveSelectedItems(
  items: readonly FileItem[],
  selectedIds: ReadonlySet<string>,
): FileItem[] {
  return items.filter(item => selectedIds.has(item.id));
}

/** AC-18.2：操作后选择态清零。 */
export function clearSelection(selectedIds: ReadonlySet<string>): Set<string> {
  return selectedIds.size === 0 ? (selectedIds as Set<string>) : new Set<string>();
}
