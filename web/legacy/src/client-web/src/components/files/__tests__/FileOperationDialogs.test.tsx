import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { DeleteDialog, MoveDialog, NameDialog, ShareDialog, SHARE_EXPIRATION_CHOICES } from '../FileOperationDialogs';
import type { FileItem, FileShare } from '../../../types';

/**
 * REQ-15 / REQ-16 / REQ-17 / REQ-21 的对话框行为。
 * 重点是**反面**：非法输入被拦、取消不产生副作用、失败不显示成功。
 */
function item(name = 'a.txt', itemType = 'file'): FileItem {
  return {
    id: `id-${name}`,
    providerId: 'p-1',
    externalFileId: 'ext-1',
    parentExternalFileId: null,
    path: `/${name}`,
    name,
    itemType,
    mimeType: 'text/plain',
    size: 100,
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

describe('NameDialog / REQ-15', () => {
  it('提交合法名称会回调规范化的值', async () => {
    const onSubmit = vi.fn(async () => {});
    render(
      <NameDialog title="重命名" initialValue="旧名.txt" submitLabel="保存" onCancel={vi.fn()} onSubmit={onSubmit} testId="rename-dialog" />,
    );

    fireEvent.change(screen.getByLabelText('名称'), { target: { value: '  新名.txt  ' } });
    fireEvent.click(screen.getByTestId('rename-dialog-submit'));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('新名.txt'));
  });

  it('AC-15.2 非法名称给出可读错误且**不提交**', async () => {
    const onSubmit = vi.fn(async () => {});
    render(
      <NameDialog title="重命名" initialValue="" submitLabel="保存" onCancel={vi.fn()} onSubmit={onSubmit} testId="rename-dialog" />,
    );

    fireEvent.change(screen.getByLabelText('名称'), { target: { value: 'a/b.txt' } });
    fireEvent.click(screen.getByTestId('rename-dialog-submit'));

    expect(onSubmit).not.toHaveBeenCalled();
    expect(screen.getByTestId('rename-dialog-error').textContent).toContain('不能包含');
  });

  it('AC-15.2 空名称给出可读错误', async () => {
    const onSubmit = vi.fn(async () => {});
    render(
      <NameDialog title="新建文件夹" initialValue="" submitLabel="创建" onCancel={vi.fn()} onSubmit={onSubmit} testId="new-folder-dialog" />,
    );

    fireEvent.click(screen.getByTestId('new-folder-dialog-submit'));

    expect(onSubmit).not.toHaveBeenCalled();
    expect(screen.getByTestId('new-folder-dialog-error').textContent).toBe('名称不能为空');
  });

  it('失败时显示可读原因且不关闭（用户无需重新输入）', async () => {
    const onSubmit = vi.fn(async () => {
      throw new Error('已存在同名条目且无法改名');
    });
    render(
      <NameDialog title="重命名" initialValue="a.txt" submitLabel="保存" onCancel={vi.fn()} onSubmit={onSubmit} testId="rename-dialog" />,
    );

    fireEvent.click(screen.getByTestId('rename-dialog-submit'));

    await waitFor(() => expect(screen.getByTestId('rename-dialog-error').textContent).toContain('已存在同名条目'));
    // 仍在对话框内（输入保留）
    expect(screen.getByLabelText('名称')).toBeTruthy();
  });
});

describe('MoveDialog / REQ-16', () => {
  const folders = [
    { path: '/', name: 'OneDrive' },
    { path: '/文档', name: '文档' },
    { path: '/图片', name: '图片' },
  ];

  it('选择目标后回调目标路径', async () => {
    const onMove = vi.fn(async () => {});
    render(<MoveDialog item={item('a.txt')} folders={folders} onCancel={vi.fn()} onMove={onMove} />);

    fireEvent.change(screen.getByLabelText('目标文件夹'), { target: { value: '/图片' } });
    fireEvent.click(screen.getByTestId('move-dialog-submit'));

    await waitFor(() => expect(onMove).toHaveBeenCalledWith('/图片'));
  });

  it('反面：未选目标时给出可读错误且不提交', async () => {
    const onMove = vi.fn(async () => {});
    render(<MoveDialog item={item('a.txt')} folders={folders} onCancel={vi.fn()} onMove={onMove} />);

    fireEvent.click(screen.getByTestId('move-dialog-submit'));

    expect(onMove).not.toHaveBeenCalled();
    expect(screen.getByTestId('move-dialog-error').textContent).toContain('请选择目标文件夹');
  });

  it('AC-16.3 反面：不能把文件夹移动到自身或自己的子孙（避免「两处都没有」）', () => {
    const folder = item('文档', 'folder');
    folder.path = '/文档';
    render(
      <MoveDialog
        item={folder}
        folders={[...folders, { path: '/文档/子', name: '子' }]}
        onCancel={vi.fn()}
        onMove={vi.fn(async () => {})}
      />,
    );

    const options = Array.from(screen.getByLabelText('目标文件夹').querySelectorAll('option')).map(o => o.value);
    expect(options).not.toContain('/文档');
    expect(options).not.toContain('/文档/子');
  });
});

describe('DeleteDialog / REQ-17', () => {
  it('AC-17.1 文案含文件名与还原指引', () => {
    render(<DeleteDialog items={[item('报告.pdf')]} onCancel={vi.fn()} onConfirm={vi.fn(async () => {})} />);

    const message = screen.getByTestId('delete-dialog-message').textContent ?? '';
    expect(message).toContain('报告.pdf');
    expect(message).toContain('OneDrive 网页版');
  });

  it('AC-17.2 反面：取消不触发删除', () => {
    const onConfirm = vi.fn(async () => {});
    render(<DeleteDialog items={[item('a.txt')]} onCancel={vi.fn()} onConfirm={onConfirm} />);

    fireEvent.click(screen.getByTestId('delete-dialog-cancel'));

    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('确认才执行删除', async () => {
    const onConfirm = vi.fn(async () => {});
    render(<DeleteDialog items={[item('a.txt')]} onCancel={vi.fn()} onConfirm={onConfirm} />);

    fireEvent.click(screen.getByTestId('delete-dialog-confirm'));

    await waitFor(() => expect(onConfirm).toHaveBeenCalledTimes(1));
  });

  it('批量删除时文案含数量', () => {
    render(<DeleteDialog items={[item('a'), item('b'), item('c')]} onCancel={vi.fn()} onConfirm={vi.fn(async () => {})} />);

    expect(screen.getByTestId('delete-dialog-message').textContent).toContain('3 项');
  });

  it('删除失败显示可读原因（不显示成功）', async () => {
    const onConfirm = vi.fn(async () => {
      throw new Error('文件已被锁定');
    });
    render(<DeleteDialog items={[item('a.txt')]} onCancel={vi.fn()} onConfirm={onConfirm} />);

    fireEvent.click(screen.getByTestId('delete-dialog-confirm'));

    await waitFor(() => expect(screen.getByTestId('delete-dialog-error').textContent).toContain('锁定'));
  });
});

describe('ShareDialog / REQ-21', () => {
  const share: FileShare = {
    itemId: 'id-a.txt',
    itemName: 'a.txt',
    path: '/a.txt',
    permissionType: 'view',
    permissionId: 'perm-1',
    webUrl: 'https://1drv.ms/view/a',
    expiresAt: null,
    createdAt: '2026-09-24T00:00:00Z',
  };

  it('P6 提供 无 / 7 天 / 30 天 三档', () => {
    render(
      <ShareDialog item={item()} onCancel={vi.fn()} onCreate={vi.fn(async () => share)} onRevoke={vi.fn(async () => {})} existing={[]} />,
    );

    expect(SHARE_EXPIRATION_CHOICES.map(c => c.value)).toEqual([0, 7, 30]);
    for (const choice of SHARE_EXPIRATION_CHOICES) {
      expect(screen.getByTestId(`share-expiration-${choice.value}`)).toBeTruthy();
    }
  });

  it('默认「可看」，可切到「可编辑」并作为入参传给 onCreate', async () => {
    const onCreate = vi.fn(async () => share);
    render(<ShareDialog item={item()} onCancel={vi.fn()} onCreate={onCreate} onRevoke={vi.fn(async () => {})} existing={[]} />);

    fireEvent.click(screen.getByTestId('share-permission-edit'));
    fireEvent.click(screen.getByTestId('share-expiration-30'));
    fireEvent.click(screen.getByTestId('share-create'));

    await waitFor(() => expect(onCreate).toHaveBeenCalledWith('edit', 30));
  });

  it('生成后展示链接与权限档', async () => {
    render(
      <ShareDialog item={item()} onCancel={vi.fn()} onCreate={vi.fn(async () => share)} onRevoke={vi.fn(async () => {})} existing={[]} />,
    );

    fireEvent.click(screen.getByTestId('share-create'));

    await waitFor(() => expect(screen.getByTestId('share-result')).toBeTruthy());
    expect((screen.getByTestId('share-link') as HTMLInputElement).value).toBe('https://1drv.ms/view/a');
  });

  it('AC-21.3 已有分享可就地撤销', async () => {
    const onRevoke = vi.fn(async () => {});
    render(<ShareDialog item={item()} onCancel={vi.fn()} onCreate={vi.fn(async () => share)} onRevoke={onRevoke} existing={[share]} />);

    fireEvent.click(screen.getByTestId('share-revoke-perm-1'));

    await waitFor(() => expect(onRevoke).toHaveBeenCalledWith('perm-1'));
  });

  it('AC-21.4 反面：生成失败时显示可读原因（敏感路径等）', async () => {
    const onCreate = vi.fn(async () => {
      throw new Error('敏感路径受保护，不允许分享');
    });
    render(<ShareDialog item={item()} onCancel={vi.fn()} onCreate={onCreate} onRevoke={vi.fn(async () => {})} existing={[]} />);

    fireEvent.click(screen.getByTestId('share-create'));

    await waitFor(() => expect(screen.getByTestId('share-dialog-error').textContent).toContain('敏感路径'));
    // 不得显示成功结果
    expect(screen.queryByTestId('share-result')).toBeNull();
  });

  it('V2 未验证时说明可能不支持有效期（不假装支持）', () => {
    render(
      <ShareDialog
        item={item()}
        onCancel={vi.fn()}
        onCreate={vi.fn(async () => share)}
        onRevoke={vi.fn(async () => {})}
        existing={[]}
        expirationSupported={false}
      />,
    );

    expect(screen.getByTestId('share-expiration-note').textContent).toContain('撤销');
  });
});
