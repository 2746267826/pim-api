import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function read(path: string): string {
  return readFileSync(path, 'utf8');
}

/** 去掉块注释与行注释，避免注释里的说明文字影响"界面不得提供修复入口"这类判断。 */
function readCode(path: string): string {
  return read(path)
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '');
}

function assertContains(path: string, snippets: string[]) {
  const source = read(path);
  for (const snippet of snippets) {
    assert.ok(source.includes(snippet), `${path} 应包含 ${snippet}`);
  }
}

const pagePath = 'src/client-web/src/pages/DataReliabilityPage.tsx';
const panelPath = 'src/client-web/src/components/data-reliability/DataReliabilityPanel.tsx';
const rowPath = 'src/client-web/src/components/data-reliability/DataReliabilityRuleRow.tsx';
const dialogPath = 'src/client-web/src/components/data-reliability/DataReliabilityRuleDialog.tsx';
const distributionPath = 'src/client-web/src/components/data-reliability/S2ThreeStateDistribution.tsx';
const settingsPath = 'src/client-web/src/pages/SettingsPage.tsx';
const layoutPath = 'src/client-web/src/layout/AppLayout.tsx';

// 手机端安全区与触控目标
assertContains(pagePath, ['pb-20']);
assertContains(panelPath, ['space-y-4']);
assertContains(rowPath, ['min-h-[44px]', 'flex-wrap']);
assertContains(dialogPath, ['min-h-[44px]']);
assertContains(distributionPath, ['sm:grid-cols-3']);

// 账本信息行与「窗内 / 历史欠账」两档数字都必须在 375px 下随卡片换行、不得撑出横向滚动：
// 账本行用 w-full + 小字号，尺子行沿用 flex-wrap（AC-7.2 / AC-7.7）。
const overviewPath = 'src/client-web/src/components/data-reliability/DataReliabilityOverview.tsx';
assertContains(overviewPath, ['data-reliability-ledger', 'w-full text-xs']);
assertContains(rowPath, ['窗内', '历史欠账']);
assert.ok(
  !/w-\[\d{3,}px\]/.test(read(overviewPath)),
  '账本行不得使用固定像素宽度（375px 下会破版）'
);

// 响应式：不得出现会造成横向滚动的固定宽度 / 视口宽度
for (const path of [pagePath, panelPath, rowPath, dialogPath, distributionPath]) {
  const source = read(path);
  assert.ok(!/\bw-\[100vw\]/.test(source), `${path} 不应使用 100vw 宽度`);
  assert.ok(!/\bmin-w-\[(6|7|8|9)\d{2,}px\]/.test(source), `${path} 不应使用超大固定最小宽度`);
  assert.ok(!/overflow-x-scroll/.test(source), `${path} 不应强制横向滚动`);
}

// 页面必须复用设置页的居中弹窗，且默认只读缓存结果
assertContains(pagePath, ['useQuery', 'getDataReliabilityInspection']);
assertContains(dialogPath, ['Dialog']);
assert.ok(
  !readCode(pagePath).includes('修复'),
  '页面是只读的，不得提供任何修复入口'
);

// 空态与过期态都必须给出可操作的引导，而不是让用户对着一片未知发愣
assertContains(pagePath, [
  'isInspectionStale',
  '本次体检结果已过期',
  '暂无数据：还没有可用的体检结果',
  'actions=',
  '重新体检',
]);

// 设置页入口与路由
assertContains(settingsPath, ["to: '/settings/data-reliability'", '数据可信度']);
assertContains(layoutPath, ['/settings/data-reliability', 'DataReliabilityPage']);

// 旧术语退役（AC-7.6 的源码侧守卫）：data-reliability 组件与页面里不得再出现「新增 / 存量」。
for (const path of [pagePath, panelPath, rowPath, dialogPath, distributionPath]) {
  const source = read(path);
  assert.ok(!source.includes('存量'), `${path} 不得再出现「存量」`);
  assert.ok(
    !/新增/.test(source),
    `${path} 不得再出现「新增」（历史欠账 / 窗内 才是现行术语）`
  );
}
assert.ok(!read(overviewPath).includes('存量'), `${overviewPath} 不得再出现「存量」`);

// 页面默认不触发全量扫库：查询只读缓存结果，全量体检必须挂在显式的 mutation 上
const pageCode = readCode(pagePath);
assert.ok(
  pageCode.includes('queryFn: getDataReliabilityInspection'),
  '查询应只读取最近一次体检结果'
);
assert.ok(
  pageCode.includes('mutationFn: refreshDataReliabilityInspection'),
  '重新体检应通过显式动作（mutation）触发，而不是查询时自动执行'
);
assert.ok(
  !pageCode.includes('queryFn: refreshDataReliabilityInspection'),
  '查询不得直接触发全量体检'
);

console.error('PASS: dataReliabilityResponsive');
