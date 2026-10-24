import { describe, expect, it } from 'vitest';
import { extractFiles, hasActiveTransfers, type TransferTask } from '../TransferPanel';

/**
 * REQ-11（三种入口共用同一取文件逻辑）与 REQ-13 的纯逻辑部分。
 * 拖拽 vs 剪贴板在 DataTransfer 上的表现不同，必须都覆盖。
 */
function transfer(files: File[] | null, items: { kind: string; file: File | null }[] = []) {
  return {
    files: (files ?? []) as unknown as FileList,
    items: items.map(entry => ({
      kind: entry.kind,
      getAsFile: () => entry.file,
    })) as unknown as DataTransferItemList,
  } as DataTransfer;
}

describe('TransferPanel / REQ-11 三种入口的取文件', () => {
  it('拖拽：从 dataTransfer.files 取', () => {
    const a = new File(['a'], 'a.txt');
    const b = new File(['b'], 'b.txt');

    expect(extractFiles(transfer([a, b]))).toEqual([a, b]);
  });

  it('剪贴板：files 为空时退回 items（粘贴图片常见形态）', () => {
    const pasted = new File(['x'], 'image.png');

    const result = extractFiles(transfer(null, [{ kind: 'file', file: pasted }]));

    expect(result).toEqual([pasted]);
  });

  it('反面：剪贴板里的非文件项被忽略（例如纯文本）', () => {
    const pasted = new File(['x'], 'image.png');

    const result = extractFiles(
      transfer(null, [
        { kind: 'string', file: null },
        { kind: 'file', file: pasted },
      ]),
    );

    expect(result).toEqual([pasted]);
  });

  it('反面：空/未定义输入返回空数组而不是抛错', () => {
    expect(extractFiles(null)).toEqual([]);
    expect(extractFiles(undefined)).toEqual([]);
    expect(extractFiles(transfer(null))).toEqual([]);
  });
});

describe('TransferPanel / REQ-13 面板状态', () => {
  function task(state: TransferTask['state']): TransferTask {
    return { id: state, fileName: `${state}.txt`, size: 1, progress: 0, state, path: '/' };
  }

  it('有进行中任务时视为活跃（面板显示/阻止关闭提示）', () => {
    expect(hasActiveTransfers([task('uploading')])).toBe(true);
    expect(hasActiveTransfers([task('queued')])).toBe(true);
  });

  it('只有历史记录时不算活跃（AC-13.3：历史可查，不阻塞）', () => {
    expect(hasActiveTransfers([task('completed'), task('failed'), task('interrupted')])).toBe(false);
  });
});
