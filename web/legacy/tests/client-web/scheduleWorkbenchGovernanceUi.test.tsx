import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function assertPageSourceContains(path: string, snippets: string[]) {
  const source = readFileSync(path, 'utf8');

  for (const snippet of snippets) {
    assert.ok(source.includes(snippet), `${path} should contain ${snippet}`);
  }
}

assertPageSourceContains('src/client-web/src/pages/SyncPage.tsx', [
  '设备代码',
  'pollOutlookDeviceCode',
  '应用注册',
  '公共客户端流',
  'Calendars.ReadWrite',
  '发现日历',
  '立即同步',
  '深度同步',
  '移除本地 Microsoft 数据',
]);

{
  const source = readFileSync('src/client-web/src/pages/SyncPage.tsx', 'utf8');
  const removed = ['tokenHealth', 'OutlookConflictResolver', 'deltaLink', 'writebackDefault', 'conflictPolicy'];
  for (const snippet of removed) {
    assert.ok(!source.includes(snippet), `SyncPage should NOT contain ${snippet}`);
  }
}

assertPageSourceContains('src/client-web/src/pages/ConfirmationsPage.tsx', [
  'BeforeAfterDiff',
  'StrictConfirmationPanel',
  '二级确认',
  'allowedActions',
]);

{
  const source = readFileSync('src/client-web/src/pages/ConfirmationsPage.tsx', 'utf8');
  assert.ok(!source.includes('<pre'),
    'ConfirmationsPage must never render raw JSON <pre>');
  assert.ok(!source.includes('active.previewJson ||'),
    'ConfirmationsPage must not embed raw previewJson content');
  assert.ok(source.includes('操作预览'),
    'ConfirmationsPage must keep an operation preview summary panel');
  assert.ok(source.includes('原始内容已隐藏'),
    'ConfirmationsPage preview summary must state that raw content is hidden');
}

assertPageSourceContains('src/client-web/src/pages/DataCenterPage.tsx', [
  'DataCenterBatchPreview',
  '审计导出',
  '版本恢复',
  'Outlook-only',
]);

assertPageSourceContains('src/client-web/src/pages/RemindersPage.tsx', [
  '提醒中心',
  'DND',
  '发送历史',
  '操作按钮',
]);

assertPageSourceContains('src/client-web/src/pages/ReportsPage.tsx', [
  '日报',
  '周报',
  '月报',
  '项目报告',
  '后续确认',
]);

assertPageSourceContains('src/client-web/src/pages/AuditTimelinePage.tsx', [
  '恢复预览',
  '导出审计',
]);
