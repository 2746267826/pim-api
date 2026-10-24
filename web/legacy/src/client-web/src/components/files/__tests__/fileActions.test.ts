import { describe, expect, it } from 'vitest';
import {
  DOWNLOAD_CONFIRM_THRESHOLD_BYTES,
  UPLOAD_MAX_BYTES,
  clearSelection,
  deleteConfirmationMessage,
  describeBatchOutcome,
  exceedsUploadLimit,
  formatBytes,
  predictRenamedName,
  requiresDownloadConfirmation,
  resolveSelectedItems,
  rowActionsFor,
  summarizeBatchResults,
  validateEntryName,
} from '../fileActions';
import type { FileItem } from '../../types';

function item(id: string, name: string, itemType = 'file'): FileItem {
  return {
    id,
    providerId: 'p-1',
    externalFileId: `ext-${id}`,
    parentExternalFileId: null,
    path: `/${name}`,
    name,
    itemType,
    mimeType: itemType === 'folder' ? null : 'text/plain',
    size: itemType === 'folder' ? null : 1024,
    etag: null,
    contentHash: null,
    currentVersionId: null,
    permissions: null,
    isDeleted: false,
    deletedAt: null,
    lastSeenAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    modifiedAt: '2026-09-01T00:00:00Z',
    syncedAt: '2026-09-01T00:00:00Z',
    indexStatus: 'not_indexed',
    ai: null,
  };
}

describe('fileActions / REQ-15 名称校验（AC-15.2）', () => {
  it('合法名称通过', () => {
    expect(validateEntryName('报告.pdf')).toBeNull();
    expect(validateEntryName('新建文件夹')).toBeNull();
    expect(validateEntryName('a (1).txt')).toBeNull();
  });

  it('反面：空名 / 纯空白被拒绝且给可读原因', () => {
    expect(validateEntryName('')).toBe('名称不能为空');
    expect(validateEntryName('   ')).toBe('名称不能为空');
    expect(validateEntryName(null)).toBe('名称不能为空');
  });

  it.each(['\\', '/', ':', '*', '?', '"', '<', '>', '|'])(
    '反面：含非法字符 %s 被拒绝',
    char => {
      const error = validateEntryName(`a${char}b.txt`);
      expect(error).not.toBeNull();
      expect(error).toContain('不能包含');
    },
  );

  it('反面：以句点结尾被拒绝', () => {
    expect(validateEntryName('报告.')).toContain('句点或空格');
  });

  it('首尾空白被规范化后再判定：纯空白非法、含内容则合法', () => {
    // 与后端 OneDriveNameValidator 一致：先 Trim 再校验，
    // 因此 "报告 " 等价于 "报告"（合法），而不是以空格结尾的非法名。
    expect(validateEntryName('报告 ')).toBeNull();
    expect(validateEntryName('   ')).toBe('名称不能为空');
    // Trim 之后仍以句点结尾的才拒绝
    expect(validateEntryName(' 报告. ')).toContain('句点或空格');
  });

  it('反面：保留名被拒绝（含带扩展名的形式）', () => {
    expect(validateEntryName('CON')).toContain('保留名称');
    expect(validateEntryName('con.txt')).toContain('保留名称');
    expect(validateEntryName('desktop.ini')).toContain('保留名称');
  });

  it('反面：超长名称被拒绝', () => {
    expect(validateEntryName('a'.repeat(256))).toContain('过长');
  });
});

describe('fileActions / REQ-12 重名自动改名', () => {
  it('不重名时保持原名', () => {
    expect(predictRenamedName('报告.pdf', ['别的.pdf'])).toBe('报告.pdf');
  });

  // 格式按真实账号实测：Graph 用「名称 1.ext」（空格 + 序号，无括号）。
  // 此前按「名称 (1).ext」预测，与实测不符（验收备注项）。
  it('AC-12.1 重名时生成「名称 1.ext」，且保留扩展名', () => {
    expect(predictRenamedName('报告.pdf', ['报告.pdf'])).toBe('报告 1.pdf');
  });

  it('连续重名时递增序号', () => {
    expect(predictRenamedName('报告.pdf', ['报告.pdf', '报告 1.pdf'])).toBe('报告 2.pdf');
  });

  it('大小写不敏感地判重（OneDrive 行为）', () => {
    expect(predictRenamedName('Report.PDF', ['report.pdf'])).toBe('Report 1.PDF');
  });

  it('无扩展名的文件也能改名', () => {
    expect(predictRenamedName('README', ['README'])).toBe('README 1');
  });
});

describe('fileActions / REQ-20 下载阈值（P4 = 100MB）', () => {
  it('AC-20.2 超过阈值需要先确认', () => {
    expect(requiresDownloadConfirmation(DOWNLOAD_CONFIRM_THRESHOLD_BYTES)).toBe(false);
    expect(requiresDownloadConfirmation(DOWNLOAD_CONFIRM_THRESHOLD_BYTES + 1)).toBe(true);
    expect(requiresDownloadConfirmation(500 * 1024 * 1024)).toBe(true);
  });

  it('反面：未知大小不弹确认（避免无谓打断）', () => {
    expect(requiresDownloadConfirmation(null)).toBe(false);
    expect(requiresDownloadConfirmation(undefined)).toBe(false);
  });

  it('大小文案对确认弹窗与列表一致', () => {
    expect(formatBytes(500 * 1024 * 1024)).toBe('500.0 MB');
    expect(formatBytes(2 * 1024 * 1024 * 1024)).toBe('2.00 GB');
    expect(formatBytes(null)).toBe('—');
  });
});

describe('fileActions / P5 上传上限 2GB', () => {
  it('超过 2GB 判定超限（提示改用 OneDrive 客户端）', () => {
    expect(exceedsUploadLimit(UPLOAD_MAX_BYTES)).toBe(false);
    expect(exceedsUploadLimit(UPLOAD_MAX_BYTES + 1)).toBe(true);
  });
});

describe('fileActions / REQ-19 行内菜单（AC-19.1）', () => {
  it('文件夹没有「下载」，有进入/删除/重命名/移动/在 OneDrive 打开', () => {
    const actions = rowActionsFor({ itemType: 'folder' });
    expect(actions).not.toContain('download');
    expect(actions).toEqual(expect.arrayContaining(['open', 'rename', 'move', 'delete', 'open-in-onedrive']));
  });

  it('文件有「下载」但不含「进入」', () => {
    const actions = rowActionsFor({ itemType: 'file' });
    expect(actions).toContain('download');
    expect(actions).not.toContain('open');
  });

  it('两者都有分享', () => {
    expect(rowActionsFor({ itemType: 'file' })).toContain('share');
    expect(rowActionsFor({ itemType: 'folder' })).toContain('share');
  });
});

describe('fileActions / REQ-17 删除二次确认（AC-17.1）', () => {
  it('单个条目：文案含文件名与还原指引', () => {
    const message = deleteConfirmationMessage(['报告.pdf']);
    expect(message).toContain('报告.pdf');
    expect(message).toContain('OneDrive 网页版');
    expect(message).toContain('回收站');
  });

  it('批量：文案含数量与还原指引', () => {
    const message = deleteConfirmationMessage(['a.txt', 'b.txt', 'c.txt']);
    expect(message).toContain('3 项');
    expect(message).toContain('还原');
  });
});

describe('fileActions / REQ-18 批量（AC-18.1 / AC-18.2）', () => {
  it('AC-18.1 全部成功时摘要为完成并给出数量', () => {
    const outcome = summarizeBatchResults([
      { item: item('1', 'a.txt') },
      { item: item('2', 'b.txt') },
      { item: item('3', 'c.txt') },
    ]);
    expect(outcome.succeeded).toHaveLength(3);
    expect(outcome.failed).toHaveLength(0);
    expect(describeBatchOutcome(outcome, '删除')).toBe('删除完成：3 项');
  });

  it('AC-18.1 部分失败时逐项列出失败条目与原因', () => {
    const outcome = summarizeBatchResults([
      { item: item('1', 'a.txt') },
      { item: item('2', 'locked.docx'), error: '文件已被锁定' },
      { item: item('3', 'c.txt') },
    ]);
    const summary = describeBatchOutcome(outcome, '删除');
    expect(summary).toContain('部分完成');
    expect(summary).toContain('锁定');
    expect(summary).toContain('locked.docx');
    expect(summary).toContain('文件已被锁定');
  });

  it('AC-18.1 全部失败时也如实报告（不谎报成功）', () => {
    const outcome = summarizeBatchResults([
      { item: item('1', 'a.txt'), error: '权限不足' },
    ]);
    const summary = describeBatchOutcome(outcome, '删除');
    expect(summary).toContain('失败');
    expect(summary).not.toContain('完成');
  });

  it('AC-18.2 只操作勾选项，未勾选项绝不出现在结果里', () => {
    const items = [item('1', 'a.txt'), item('2', 'b.txt'), item('3', 'c.txt')];
    const selected = new Set(['1', '3']);

    const resolved = resolveSelectedItems(items, selected);

    expect(resolved.map(i => i.id)).toEqual(['1', '3']);
    expect(resolved.some(i => i.id === '2')).toBe(false);
  });

  it('AC-18.2 操作后选择态清零', () => {
    const cleared = clearSelection(new Set(['1', '2']));
    expect(cleared.size).toBe(0);
  });

  it('反面：已不存在的勾选项不会产生请求（避免对空气发操作）', () => {
    const items = [item('1', 'a.txt')];
    const resolved = resolveSelectedItems(items, new Set(['1', 'gone']));
    expect(resolved).toHaveLength(1);
  });
});
