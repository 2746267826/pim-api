/**
 * 今日页布局回归护栏（v4：三层分区 + 14 模块全量接入）。
 *
 * 信息架构（2026-09-19 重排）：
 *   action（首屏，行动）→ data（数据回顾）→ status（运维与状态，折叠收纳）。
 *
 * #285 的历史约束在新布局下依然要守住：
 *   1. 每个区的容器必须显式 items-start（取消 CSS Grid 强制等高拉伸）；
 *   2. 长列表板块（任务关注 / 分类建议）必须自带独立滚动容器；
 *   3. 区块种类不得缩水；运营模块只做收纳、不得删除。
 *
 * v3 变更（用户拍板）：
 *   - 移除顶部「新建任务」按钮与密度切换器（只保留默认布局）；
 *   - 未注册区块被过滤，不再渲染「未知区块」占位。
 * v4 变更（2026-09-19 用户拍板「AB 类都放出来」）：
 *   - 8 个此前未接入的注册模块全量放出来（A 类：服务端就绪；B 类：待服务端修复）；
 *   - 临时硬编码的运营卡删除，统一由注册表渲染管线接管（同一功能一条路径）。
 */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { todayZoneOf } from '../../src/client-web/src/pages/todaySectionLayout';

function test(name: string, run: () => void) { run(); }

const read = (relative: string) => readFileSync(path.join(process.cwd(), relative), 'utf8');
const todayPage = read('src/client-web/src/pages/TodayPage.tsx');

test('三层分区映射：行动 / 数据 / 状态', () => {
  assert.equal(todayZoneOf('calendar.schedule'), 'action');
  assert.equal(todayZoneOf('calendar.tasks'), 'action');
  assert.equal(todayZoneOf('pc.classification_suggestions'), 'action');
  assert.equal(todayZoneOf('pc.activity'), 'data');
  assert.equal(todayZoneOf('operations.health'), 'status');
  assert.equal(todayZoneOf('pc.quality'), 'status');
  // v4：全量接入的 8 个模块统一收纳在折叠区
  for (const kind of [
    'operations.confirmations',
    'sync.outlook',
    'reminders.queue',
    'reports.available',
    'endpoints.status',
    'calendar.availability',
    'calendar.habits',
    'calendar.ai_placeholders',
  ]) {
    assert.equal(todayZoneOf(kind), 'status', `${kind} 应归入 status`);
  }
  // 未知区块归入 status（折叠收纳），不占首屏
  assert.equal(todayZoneOf('unknown.section'), 'status');
});

test('#285 区块容器显式 items-start（取消 CSS Grid 强制等高拉伸）', () => {
  assert.ok(/items-start/.test(todayPage), '今日页区块容器必须显式 items-start');
});

test('#285 长列表板块拥有独立滚动容器', () => {
  const taskColumn = read('src/client-web/src/components/today/TodayTaskColumn.tsx');
  assert.ok(
    /max-h-\[/.test(taskColumn) && /overflow-y-auto/.test(taskColumn),
    '「任务关注」应有 max-h-* + overflow-y-auto 的独立滚动容器',
  );
});

test('未注册区块被过滤（不再渲染「未知区块」占位）', () => {
  assert.ok(
    /\.filter\(section => isKnownTodaySectionKind\(section\.kind\)\)/.test(todayPage),
    'TodayPage 应过滤未注册的区块（防止「未知区块」占用页面）',
  );
});

test('顶部工具区已精简（移除新建任务按钮与密度切换）', () => {
  assert.ok(!todayPage.includes('densityMode'), '不应再使用密度切换状态');
  assert.ok(!todayPage.includes('SegmentedControl'), '不应再渲染密度切换器');
});

test('三个分区在页面中都有渲染', () => {
  for (const zone of ['actionSections', 'dataSections', 'statusSections']) {
    assert.ok(todayPage.includes(zone), `TodayPage 应渲染 ${zone}`);
  }
});

test('运维与状态为折叠收纳（details/summary）', () => {
  assert.ok(/<details/.test(todayPage) && /pim-collapsible/.test(todayPage), '运维区应为可折叠 details');
});

test('展示的区块种类未缩水', () => {
  const host = read('src/client-web/src/components/today/TodaySectionHost.tsx');
  for (const kind of [
    'calendar.schedule',
    'pc.activity',
    'calendar.tasks',
    'operations.health',
    'pc.quality',
    'pc.classification_suggestions',
  ]) {
    assert.ok(host.includes(kind), `区块种类 ${kind} 应保留`);
  }
});

test('运营模块只做收纳、不得删除（v4：由注册表系统接管渲染）', () => {
  const host = read('src/client-web/src/components/today/TodaySectionHost.tsx');
  for (const kept of ['待确认', '微软同步', '提醒队列', '报告']) {
    assert.ok(host.includes(kept), `运营模块「${kept}」应保留（注册表渲染管线）`);
  }
});

test('v4：8 个新模块已进入注册表渲染管线与折叠区', () => {
  const host = read('src/client-web/src/components/today/TodaySectionHost.tsx');
  const layout = read('src/client-web/src/pages/todaySectionLayout.ts');
  for (const kind of [
    'operations.confirmations',
    'sync.outlook',
    'reminders.queue',
    'reports.available',
    'endpoints.status',
    'calendar.availability',
    'calendar.habits',
    'calendar.ai_placeholders',
  ]) {
    assert.ok(host.includes(kind), `区块 ${kind} 应出现在 TodaySectionHost`);
    assert.ok(layout.includes(kind), `区块 ${kind} 应出现在 todaySectionLayout`);
  }
});

test('其他既有能力未被破坏', () => {
  for (const kept of ['日程任务工作台']) {
    assert.ok(todayPage.includes(kept), `今日页应保留「${kept}」`);
  }
});

console.log('todayLayout tests passed');
