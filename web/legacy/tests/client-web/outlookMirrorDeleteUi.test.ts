import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

/**
 * #309 UI 回归：远端已删除的日历现在是「跟随删除」——弹窗条目消失、同步历史里
 * 明确说明「已移入回收站」，而不是报一个重试无效的错误。
 *
 * 这些断言故意写得"结构 + 文案"并重：只锁结构会漏掉用户实际读到的话，
 * 只锁文案会在重构（改名/换组件）时误报。
 */
const syncPageSource = readFileSync(
  new URL('../../src/client-web/src/pages/SyncPage.tsx', import.meta.url),
  'utf8',
);

const recycleBinSource = readFileSync(
  new URL('../../src/client-web/src/pages/RecycleBinPage.tsx', import.meta.url),
  'utf8',
);

// --- 同步历史必须展示跟随删除的结果 ---
assert.match(
  syncPageSource,
  /step\.status === 'mirror-deleted'/,
  '同步历史应筛选出 mirror-deleted 步骤',
);
assert.match(syncPageSource, /已在 Outlook 端删除/, '应明确告知用户日历已在 Outlook 端删除');
assert.match(syncPageSource, /移入回收站/, '应告知数据去向是回收站');
assert.match(syncPageSource, /恢复后它们会作为 PIM 本地日历使用/, '应说明恢复语义（转为本地日历）');
assert.match(syncPageSource, /不再与 Outlook 关联/, '应说明恢复后与 Outlook 脱钩');

// --- 该提示是"成功结果"而非错误：不能挂在红色错误样式上 ---
assert.doesNotMatch(
  syncPageSource,
  /mirror-deleted[\s\S]{0,200}bg-red-50/,
  'mirror-deleted 不能渲染成错误样式',
);

// --- 「缺失」不再是终态：绑定列表刷新逻辑必须保留 ---
assert.match(syncPageSource, /async function refreshBindings\(\)/);
assert.match(syncPageSource, /await outlookBindings\(\)[\s\S]{0,200}setBindings\(data\)/);
assert.equal(
  (syncPageSource.match(/void refreshBindings\(\);/g) ?? []).length,
  2,
  '同步后与单日历重试后都要刷新绑定列表',
);

// --- 回收站页面存在，用户能真的找到数据 ---
assert.match(
  recycleBinSource,
  /recycle|回收站/i,
  '回收站页面应可用',
);

// --- 文案不应再把「缺失」当作需要用户去 Outlook 恢复的终态错误 ---
assert.doesNotMatch(
  syncPageSource,
  /该日历在 Outlook 端已不存在，重试无效/,
  '旧策略的「重试无效」提示应随策略变更移除',
);
