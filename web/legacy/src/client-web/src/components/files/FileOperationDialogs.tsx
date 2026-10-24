import { useMemo, useState } from 'react';
import { AlertTriangle, Link2, Loader2 } from 'lucide-react';
import type { FileItem, FileShare } from '../../types';
import { deleteConfirmationMessage, formatBytes, validateEntryName } from './fileActions';

/**
 * 文件操作对话框（REQ-15 / REQ-16 / REQ-17 / REQ-21）。
 *
 * 共同点：
 * - 提交前做**前端可读校验**（与后端规则一致），非法名称给出可读错误并阻止提交（AC-15.2）；
 * - 提交中禁用按钮防止重复提交；
 * - 失败把可读原因显示在对话框内，**不关闭对话框**（用户不用重新输入）。
 */

interface DialogShellProps {
  title: string;
  onClose: () => void;
  children: React.ReactNode;
  testId: string;
}

function DialogShell({ title, onClose, children, testId }: DialogShellProps) {
  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/30 p-4" data-testid={testId}>
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="w-full max-w-md rounded-xl border border-[var(--pim-border)] bg-[var(--pim-surface)] p-4 shadow-[var(--pim-shadow-pop)]"
      >
        <h2 className="mb-3 text-sm font-medium">{title}</h2>
        {children}
        <button type="button" className="sr-only" onClick={onClose} data-testid={`${testId}-dismiss`}>
          关闭
        </button>
      </div>
    </div>
  );
}

export interface NameDialogProps {
  title: string;
  initialValue: string;
  submitLabel: string;
  onCancel: () => void;
  onSubmit: (name: string) => Promise<void>;
  testId: string;
}

/** 重命名 / 新建文件夹共用（REQ-15）。 */
export function NameDialog({ title, initialValue, submitLabel, onCancel, onSubmit, testId }: NameDialogProps) {
  const [value, setValue] = useState(initialValue);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    const validation = validateEntryName(value);
    if (validation) {
      // AC-15.2：可读错误 + 阻止提交
      setError(validation);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await onSubmit(value.trim());
    } catch (e) {
      setError(e instanceof Error && e.message ? e.message : '操作失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogShell title={title} onClose={onCancel} testId={testId}>
      <input
        autoFocus
        className="w-full rounded-lg border border-[var(--pim-border)] px-3 py-2 text-sm"
        aria-label="名称"
        value={value}
        onChange={e => setValue(e.target.value)}
        onKeyDown={e => {
          if (e.key === 'Enter') void submit();
        }}
      />
      {error && (
        <p className="mt-2 text-xs text-[var(--pim-danger)]" role="alert" data-testid={`${testId}-error`}>
          {error}
        </p>
      )}
      <div className="mt-4 flex justify-end gap-2">
        <button type="button" className="pim-button-secondary px-3 py-1.5 text-sm" onClick={onCancel} disabled={busy}>
          取消
        </button>
        <button
          type="button"
          className="pim-button-primary px-3 py-1.5 text-sm"
          onClick={() => void submit()}
          disabled={busy}
          data-testid={`${testId}-submit`}
        >
          {busy ? <Loader2 size={13} className="animate-spin" /> : submitLabel}
        </button>
      </div>
    </DialogShell>
  );
}

export interface MoveDialogProps {
  item: FileItem;
  /** 可选目标目录（来自已加载的树数据）。 */
  folders: { path: string; name: string }[];
  onCancel: () => void;
  onMove: (destinationPath: string) => Promise<void>;
}

/** 移动（REQ-16：弹窗选择目标，所有设备可用）。 */
export function MoveDialog({ item, folders, onCancel, onMove }: MoveDialogProps) {
  const [destination, setDestination] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const options = useMemo(
    () => folders.filter(folder => folder.path !== item.path && !folder.path.startsWith(`${item.path}/`)),
    [folders, item.path],
  );

  const submit = async () => {
    if (!destination) {
      setError('请选择目标文件夹');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await onMove(destination);
    } catch (e) {
      setError(e instanceof Error && e.message ? e.message : '移动失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogShell title={`移动「${item.name}」`} onClose={onCancel} testId="move-dialog">
      <label className="block text-xs text-[var(--pim-text-muted)]" htmlFor="move-target">
        目标文件夹
      </label>
      <select
        id="move-target"
        aria-label="目标文件夹"
        className="mt-1 w-full rounded-lg border border-[var(--pim-border)] px-3 py-2 text-sm"
        value={destination}
        onChange={e => setDestination(e.target.value)}
      >
        <option value="">请选择…</option>
        {options.map(folder => (
          <option key={folder.path} value={folder.path}>
            {folder.path === '/' ? 'OneDrive（根目录）' : folder.path}
          </option>
        ))}
      </select>
      {error && (
        <p className="mt-2 text-xs text-[var(--pim-danger)]" role="alert" data-testid="move-dialog-error">
          {error}
        </p>
      )}
      <div className="mt-4 flex justify-end gap-2">
        <button type="button" className="pim-button-secondary px-3 py-1.5 text-sm" onClick={onCancel} disabled={busy}>
          取消
        </button>
        <button
          type="button"
          className="pim-button-primary px-3 py-1.5 text-sm"
          onClick={() => void submit()}
          disabled={busy}
          data-testid="move-dialog-submit"
        >
          {busy ? <Loader2 size={13} className="animate-spin" /> : '移动'}
        </button>
      </div>
    </DialogShell>
  );
}

export interface DeleteDialogProps {
  items: FileItem[];
  onCancel: () => void;
  onConfirm: () => Promise<void>;
}

/**
 * 删除二次确认（REQ-17 / AC-17.1）。
 * 文案必须含还原指引：OneDrive 个人版没有回收站 API，PIM 内无法还原。
 */
export function DeleteDialog({ items, onCancel, onConfirm }: DeleteDialogProps) {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      await onConfirm();
    } catch (e) {
      setError(e instanceof Error && e.message ? e.message : '删除失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogShell title="确认删除" onClose={onCancel} testId="delete-dialog">
      <div className="flex items-start gap-2 rounded-lg bg-[var(--pim-danger-soft)] p-3 text-xs text-[var(--pim-danger)]">
        <AlertTriangle size={14} className="mt-0.5 shrink-0" />
        <span data-testid="delete-dialog-message">{deleteConfirmationMessage(items.map(i => i.name))}</span>
      </div>
      {error && (
        <p className="mt-2 text-xs text-[var(--pim-danger)]" role="alert" data-testid="delete-dialog-error">
          {error}
        </p>
      )}
      <div className="mt-4 flex justify-end gap-2">
        <button type="button" className="pim-button-secondary px-3 py-1.5 text-sm" onClick={onCancel} data-testid="delete-dialog-cancel">
          取消
        </button>
        <button
          type="button"
          className="pim-button-primary px-3 py-1.5 text-sm"
          onClick={() => void confirm()}
          disabled={busy}
          data-testid="delete-dialog-confirm"
        >
          {busy ? <Loader2 size={13} className="animate-spin" /> : '删除'}
        </button>
      </div>
    </DialogShell>
  );
}

/** P6：分享有效期选项（无 / 7 天 / 30 天）。 */
export const SHARE_EXPIRATION_CHOICES = [
  { value: 0, label: '无有效期' },
  { value: 7, label: '7 天' },
  { value: 30, label: '30 天' },
] as const;

export interface ShareDialogProps {
  item: FileItem;
  onCancel: () => void;
  onCreate: (permissionType: 'view' | 'edit', expiresInDays: number) => Promise<FileShare>;
  onRevoke: (permissionId: string) => Promise<void>;
  /** 已有分享（AC-21.3：预览面板内也可撤销）。 */
  existing: FileShare[];
  /** V2 未验证时的说明：平台若不支持有效期，UI 必须说明而不是假装支持。 */
  expirationSupported?: boolean;
}

/**
 * 分享（REQ-21 / AC-21.3 / AC-21.4）。
 *
 * V2（个人版是否支持有效期）在无真实账号时无法验证，因此默认按「不确定」呈现：
 * 提供档位选择，但明确提示「若平台不支持将自动降级为无有效期，可随时手动撤销」，
 * 而不是静默假装成功。
 */
export function ShareDialog({
  item,
  onCancel,
  onCreate,
  onRevoke,
  existing,
  expirationSupported,
}: ShareDialogProps) {
  const [permission, setPermission] = useState<'view' | 'edit'>('view');
  const [expiresInDays, setExpiresInDays] = useState<number>(0);
  const [link, setLink] = useState<FileShare | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [shares, setShares] = useState<FileShare[]>(existing);

  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const created = await onCreate(permission, expiresInDays);
      setLink(created);
      setShares(current => [created, ...current]);
    } catch (e) {
      setError(e instanceof Error && e.message ? e.message : '生成分享链接失败');
    } finally {
      setBusy(false);
    }
  };

  const revoke = async (permissionId: string) => {
    setBusy(true);
    setError(null);
    try {
      await onRevoke(permissionId);
      setShares(current => current.filter(share => share.permissionId !== permissionId));
      setLink(current => (current?.permissionId === permissionId ? null : current));
    } catch (e) {
      setError(e instanceof Error && e.message ? e.message : '撤销失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogShell title={`分享「${item.name}」`} onClose={onCancel} testId="share-dialog">
      <div className="space-y-3 text-xs">
        <div>
          <span className="text-[var(--pim-text-muted)]">权限</span>
          <div className="mt-1 flex gap-1 rounded-lg border border-[var(--pim-border)] p-0.5">
            {(['view', 'edit'] as const).map(type => (
              <button
                key={type}
                type="button"
                className={`flex-1 rounded-md px-2 py-1 ${
                  permission === type ? 'bg-[var(--pim-primary-soft)] text-[var(--pim-primary)]' : ''
                }`}
                aria-pressed={permission === type}
                data-testid={`share-permission-${type}`}
                onClick={() => setPermission(type)}
              >
                {type === 'view' ? '可看' : '可编辑'}
              </button>
            ))}
          </div>
        </div>

        <div>
          <span className="text-[var(--pim-text-muted)]">有效期</span>
          <div className="mt-1 flex gap-1" data-testid="share-expiration">
            {SHARE_EXPIRATION_CHOICES.map(choice => (
              <button
                key={choice.value}
                type="button"
                className={`flex-1 rounded-md border border-[var(--pim-border)] px-2 py-1 ${
                  expiresInDays === choice.value ? 'bg-[var(--pim-primary-soft)] text-[var(--pim-primary)]' : ''
                }`}
                aria-pressed={expiresInDays === choice.value}
                data-testid={`share-expiration-${choice.value}`}
                onClick={() => setExpiresInDays(choice.value)}
              >
                {choice.label}
              </button>
            ))}
          </div>
          {expirationSupported === false && (
            <p className="mt-1 text-[11px] text-[var(--pim-text-muted)]" data-testid="share-expiration-note">
              个人版可能不支持有效期：若平台忽略该设置，链接将长期有效，请随时用下方「撤销」手动失效。
            </p>
          )}
          {expirationSupported === true && (
            <p className="mt-1 text-[11px] text-[var(--pim-text-muted)]" data-testid="share-expiration-note">
              有效期在个人版已实测可用（创建时返回 201）。到期后链接自动失效，也可随时用下方「撤销」提前失效。
            </p>
          )}
        </div>

        {!link && (
          <button
            type="button"
            className="pim-button-primary inline-flex w-full items-center justify-center gap-1 px-3 py-1.5"
            onClick={() => void create()}
            disabled={busy}
            data-testid="share-create"
          >
            {busy ? <Loader2 size={13} className="animate-spin" /> : <Link2 size={13} />}
            生成分享链接
          </button>
        )}

        {link && (
          <div className="rounded-lg border border-[var(--pim-border)] p-2" data-testid="share-result">
            <p className="text-[11px] text-[var(--pim-text-muted)]">
              {link.permissionType === 'edit' ? '可编辑' : '可看'}
              {link.expiresAt ? ` · 有效期至 ${new Date(link.expiresAt).toLocaleDateString('zh-CN')}` : ' · 无有效期'}
            </p>
            <div className="mt-1 flex items-center gap-2">
              <input
                readOnly
                className="min-w-0 flex-1 rounded border border-[var(--pim-border)] px-2 py-1 text-[11px]"
                value={link.webUrl}
                aria-label="分享链接"
                data-testid="share-link"
              />
              <button
                type="button"
                className="pim-button-secondary px-2 py-1 text-[11px]"
                data-testid="share-copy"
                onClick={() => {
                  void navigator.clipboard?.writeText(link.webUrl).catch(() => undefined);
                }}
              >
                复制
              </button>
            </div>
          </div>
        )}

        {shares.length > 0 && (
          <div data-testid="share-existing">
            <p className="text-[var(--pim-text-muted)]">已有的分享</p>
            <ul className="mt-1 space-y-1">
              {shares.map(share => (
                <li key={share.permissionId ?? share.webUrl} className="flex items-center gap-2">
                  <span className="min-w-0 flex-1 truncate">{share.permissionType === 'edit' ? '可编辑' : '可看'}</span>
                  <button
                    type="button"
                    className="text-[11px] text-[var(--pim-danger)] underline"
                    data-testid={`share-revoke-${share.permissionId ?? 'unknown'}`}
                    onClick={() => share.permissionId && void revoke(share.permissionId)}
                  >
                    撤销
                  </button>
                </li>
              ))}
            </ul>
          </div>
        )}

        {error && (
          <p className="text-[var(--pim-danger)]" role="alert" data-testid="share-dialog-error">
            {error}
          </p>
        )}
      </div>
      <div className="mt-4 flex justify-end">
        <button type="button" className="pim-button-secondary px-3 py-1.5 text-sm" onClick={onCancel}>
          关闭
        </button>
      </div>
    </DialogShell>
  );
}

export interface MySharesDialogProps {
  shares: FileShare[];
  loading?: boolean;
  error?: string | null;
  onRevoke: (share: FileShare) => Promise<void>;
  onClose: () => void;
  /** 打开某条分享所在目录。 */
  onReveal: (share: FileShare) => void;
}

/**
 * 「我的分享」列表（REQ-21 / AC-21.3）。
 *
 * 口径说明（必须如实告诉用户）：OneDrive 个人版**没有**「列出我的全部分享」的接口，
 * 只能按条目查询；因此这里展示的是**最近同步的候选条目中查到的分享**，
 * 并明确标注范围，而不是假装能列全。
 */
export function MySharesDialog({ shares, loading, error, onRevoke, onClose, onReveal }: MySharesDialogProps) {
  const [busy, setBusy] = useState<string | null>(null);

  return (
    <DialogShell title="我的分享" onClose={onClose} testId="my-shares-dialog">
      <p className="mb-2 text-[11px] text-[var(--pim-text-muted)]" data-testid="my-shares-scope">
        个人版没有「列出全部分享」的接口，这里显示的是最近同步的条目中查到的分享（最多 50 条）。
      </p>

      {loading && (
        <p className="py-3 text-center text-xs text-[var(--pim-text-muted)]" data-testid="my-shares-loading">
          加载中…
        </p>
      )}
      {error && (
        <p className="text-xs text-[var(--pim-danger)]" role="alert" data-testid="my-shares-error">
          {error}
        </p>
      )}

      {!loading && shares.length === 0 && (
        <p className="py-3 text-center text-xs text-[var(--pim-text-muted)]" data-testid="my-shares-empty">
          这个范围内还没有分享链接
        </p>
      )}

      <ul className="max-h-72 space-y-1 overflow-auto text-xs">
        {shares.map(share => (
          <li
            key={`${share.itemId}-${share.permissionId ?? share.webUrl}`}
            className="flex items-center gap-2 border-b border-[var(--pim-border-soft)] py-1.5"
            data-testid="my-share-row"
            data-file={share.itemName}
          >
            <span className="min-w-0 flex-1">
              <span className="block truncate" title={share.itemName}>{share.itemName}</span>
              <span className="block truncate text-[11px] text-[var(--pim-text-muted)]" title={share.path}>
                {share.path} · {share.permissionType === 'edit' ? '可编辑' : '可看'}
                {share.expiresAt ? ` · 至 ${new Date(share.expiresAt).toLocaleDateString('zh-CN')}` : ''}
              </span>
            </span>
            <button
              type="button"
              className="shrink-0 text-[11px] text-[var(--pim-primary)] underline"
              data-testid={`my-share-reveal-${share.itemId}`}
              onClick={() => onReveal(share)}
            >
              所在目录
            </button>
            <button
              type="button"
              className="shrink-0 text-[11px] text-[var(--pim-danger)] underline"
              data-testid={`my-share-revoke-${share.permissionId ?? share.itemId}`}
              disabled={busy === share.permissionId}
              onClick={async () => {
                setBusy(share.permissionId);
                try {
                  await onRevoke(share);
                } finally {
                  setBusy(null);
                }
              }}
            >
              撤销
            </button>
          </li>
        ))}
      </ul>

      <div className="mt-4 flex justify-end">
        <button type="button" className="pim-button-secondary px-3 py-1.5 text-sm" onClick={onClose}>
          关闭
        </button>
      </div>
    </DialogShell>
  );
}

export { formatBytes };
