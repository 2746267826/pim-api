import { describe, expect, it } from 'vitest';
import {
  DEFAULT_FILE_BROWSER_MEMORY,
  breadcrumbSegments,
  displayParentPath,
  isRestorablePath,
  normalizeDirPath,
  pageCountLabel,
  parentDirPath,
  parseFileBrowserMemory,
  readFileBrowserMemory,
  writeFileBrowserMemory,
  FILE_BROWSER_STORAGE_KEY,
} from '../fileBrowserState';

/**
 * REQ-1（打开落点与恢复）、REQ-9（排序/视图记忆）的持久化与路径工具。
 * 重点是 AC-1.2 的反面：记忆缺失、损坏、类型错误、存储不可用时都必须安全回退，
 * 而不是把文件页带进异常状态。
 */
describe('fileBrowserState', () => {
  describe('parseFileBrowserMemory', () => {
    it('AC-1.2 首次访问（无记忆）回到根目录且不报错', () => {
      expect(parseFileBrowserMemory(null)).toEqual(DEFAULT_FILE_BROWSER_MEMORY);
      expect(parseFileBrowserMemory(undefined)).toEqual(DEFAULT_FILE_BROWSER_MEMORY);
      expect(parseFileBrowserMemory('')).toEqual(DEFAULT_FILE_BROWSER_MEMORY);
      expect(parseFileBrowserMemory(null).path).toBe('/');
    });

    it('AC-1.2 记忆损坏（非法 JSON / 非对象）时安全回退根目录', () => {
      expect(parseFileBrowserMemory('{ not json').path).toBe('/');
      expect(parseFileBrowserMemory('"a string"').path).toBe('/');
      expect(parseFileBrowserMemory('[1,2,3]').path).toBe('/');
      expect(parseFileBrowserMemory('null').path).toBe('/');
    });

    it('AC-1.2 字段类型/取值非法时逐项回退默认值', () => {
      const parsed = parseFileBrowserMemory(
        JSON.stringify({ path: 42, view: 'hologram', sort: 'colour', order: 'sideways', searchScope: 'galaxy' }),
      );

      expect(parsed).toEqual(DEFAULT_FILE_BROWSER_MEMORY);
    });

    it('保留合法记忆：目录 / 视图 / 排序 / 搜索范围', () => {
      const parsed = parseFileBrowserMemory(
        JSON.stringify({ path: '/文档/学习资料', view: 'grid', sort: 'modified', order: 'desc', searchScope: 'global' }),
      );

      expect(parsed).toEqual({
        path: '/文档/学习资料',
        view: 'grid',
        sort: 'modified',
        order: 'desc',
        searchScope: 'global',
      });
    });

    it('AC-1.1 目录路径被规范化（反斜杠、重复斜杠、尾斜杠）', () => {
      expect(parseFileBrowserMemory(JSON.stringify({ path: '\\文档\\\\学习资料\\' })).path).toBe('/文档/学习资料');
      expect(parseFileBrowserMemory(JSON.stringify({ path: '/a/b/' })).path).toBe('/a/b');
      expect(parseFileBrowserMemory(JSON.stringify({ path: '/' })).path).toBe('/');
    });
  });

  describe('read/write storage', () => {
    it('往返读写保持记忆', () => {
      const store = new Map<string, string>();
      const storage = {
        getItem: (k: string) => store.get(k) ?? null,
        setItem: (k: string, v: string) => void store.set(k, v),
      };

      writeFileBrowserMemory({ ...DEFAULT_FILE_BROWSER_MEMORY, path: '/图片' }, storage);
      expect(store.get(FILE_BROWSER_STORAGE_KEY)).toBeTruthy();
      expect(readFileBrowserMemory(storage).path).toBe('/图片');
    });

    it('AC-1.2 存储不可用（隐私模式/配额满）时读写都不抛错', () => {
      const throwing = {
        getItem: () => {
          throw new Error('SecurityError');
        },
        setItem: () => {
          throw new Error('QuotaExceededError');
        },
      };

      expect(() => writeFileBrowserMemory(DEFAULT_FILE_BROWSER_MEMORY, throwing)).not.toThrow();
      expect(readFileBrowserMemory(throwing)).toEqual(DEFAULT_FILE_BROWSER_MEMORY);
      expect(readFileBrowserMemory(null)).toEqual(DEFAULT_FILE_BROWSER_MEMORY);
    });
  });

  describe('路径工具', () => {
    it('normalizeDirPath 统一分隔符并收敛到根目录', () => {
      expect(normalizeDirPath('')).toBe('/');
      expect(normalizeDirPath('   ')).toBe('/');
      expect(normalizeDirPath('main/SAVE')).toBe('/main/SAVE');
      expect(normalizeDirPath('/main//SAVE/')).toBe('/main/SAVE');
      expect(normalizeDirPath(undefined)).toBe('/');
    });

    it('AC-5.2 面包屑逐级可点、路径与层级一致', () => {
      expect(breadcrumbSegments('/main/SAVE/archives')).toEqual([
        { name: 'OneDrive', path: '/' },
        { name: 'main', path: '/main' },
        { name: 'SAVE', path: '/main/SAVE' },
        { name: 'archives', path: '/main/SAVE/archives' },
      ]);
      expect(breadcrumbSegments('/')).toEqual([{ name: 'OneDrive', path: '/' }]);
    });

    it('AC-5.3 「上一级」在根目录不可用，子目录逐级回退', () => {
      expect(parentDirPath('/')).toBeNull();
      expect(parentDirPath('/main')).toBe('/');
      expect(parentDirPath('/main/SAVE/archives')).toBe('/main/SAVE');
    });

    it('AC-3.1 页码文案显示真实总数与当前页', () => {
      expect(pageCountLabel(1, 85, 8465)).toBe('共 8465 项 · 第 1/85 页');
      expect(pageCountLabel(85, 85, 8465)).toBe('共 8465 项 · 第 85/85 页');
      expect(pageCountLabel(1, 0, 0)).toBe('共 0 项');
      // 页码越界时收敛到有效范围，不显示「第 900/85 页」
      expect(pageCountLabel(900, 85, 8465)).toBe('共 8465 项 · 第 85/85 页');
    });

    it('AC-1.2 记忆目录仍存在才可恢复；已改名/删除则回退根目录', () => {
      const tree = {
        '/': { items: [{ path: '/main' }], totalCount: 1 },
        '/main': { items: [{ path: '/main/SAVE' }], totalCount: 1 },
        '/main/SAVE': { items: [], totalCount: 0 },
      };

      expect(isRestorablePath('/', tree)).toBe(true);
      expect(isRestorablePath('/main/SAVE', tree)).toBe(true);
      // 父目录已全部加载，但里面没有这一项 -> 目录已被改名/删除
      expect(isRestorablePath('/main/GONE', tree)).toBe(false);
      expect(isRestorablePath('/deleted-top-level', tree)).toBe(false);
    });

    it('AC-1.2 父目录尚未加载时不误判为失效（否则刚进深目录就被弹回根）', () => {
      const tree = { '/': { items: [{ path: '/main' }], totalCount: 1 } };

      // 直接父目录 /main/SAVE 的子项还没回来：无法判定 != 失效
      expect(isRestorablePath('/main/SAVE/archives', tree)).toBe(true);
      expect(isRestorablePath('/main/SAVE/archives', { '/': { items: [{ path: '/main' }], totalCount: 1 }, '/main': { items: [], totalCount: 0 } })).toBe(true);
      // 完全空的树同理
      expect(isRestorablePath('/main/SAVE', {})).toBe(true);
    });

    it('AC-1.2 被树加载上限截断的真实目录不得被误判为已删除（复审 Important）', () => {
      // 父目录真实有 5000 个子项，树只加载了前 2000 个（截断），
      // 记忆里的目录排在 2000 名之后 —— 此时**不能**判失效，否则会把用户弹回根目录。
      const truncated = { '/big': { items: [{ path: '/big/a' }], totalCount: 5000 } };
      expect(isRestorablePath('/big/zzz-not-in-first-page', truncated)).toBe(true);

      // 只有「已全部加载且确实没有」才判失效
      const complete = { '/big': { items: [{ path: '/big/a' }], totalCount: 1 } };
      expect(isRestorablePath('/big/zzz-not-in-first-page', complete)).toBe(false);
    });

    it('AC-8.1 全局搜索结果展示所在目录', () => {
      expect(displayParentPath('/main/SAVE/a/x.jpg')).toBe('/main/SAVE/a');
      expect(displayParentPath('/x.jpg')).toBe('/');
    });
  });
});
