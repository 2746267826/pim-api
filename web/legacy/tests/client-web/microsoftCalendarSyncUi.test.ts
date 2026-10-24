import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const syncPageSource = readFileSync(
  new URL('../../src/client-web/src/pages/SyncPage.tsx', import.meta.url),
  'utf8',
);

const calendarPageSource = readFileSync(
  new URL('../../src/client-web/src/pages/CalendarPage.tsx', import.meta.url),
  'utf8',
);

const typesSource = readFileSync(
  new URL('../../src/client-web/src/types/index.ts', import.meta.url),
  'utf8',
);

const calendarApiSource = readFileSync(
  new URL('../../src/client-web/src/api/calendar.ts', import.meta.url),
  'utf8',
);

// --- Required Chinese setup guide strings in SyncPage ---
assert.match(syncPageSource, /应用注册/);
assert.match(syncPageSource, /公共客户端流/);
assert.match(syncPageSource, /Calendars\.ReadWrite/);

// --- Required action strings ---
assert.match(syncPageSource, /复制代码/);
assert.match(syncPageSource, /打开 Microsoft/);

// --- Required destructive confirmation text ---
assert.match(syncPageSource, /不修改 Outlook 云端/);
assert.match(syncPageSource, /移除本地 Microsoft 数据/);

// --- Required mode labels ---
assert.match(syncPageSource, /立即同步/);
assert.match(syncPageSource, /深度同步/);
assert.match(syncPageSource, /强制获取全部日程/);

// --- Absence of old/removed fields ---
assert.doesNotMatch(syncPageSource, /deltaLink/);
assert.doesNotMatch(syncPageSource, /writeback(Default|back)/);
assert.doesNotMatch(syncPageSource, /conflictPolicy/);
assert.doesNotMatch(syncPageSource, /tokenHealth/);
assert.doesNotMatch(syncPageSource, /\.isPaused/);
assert.doesNotMatch(syncPageSource, /\.remoteMissing/);
assert.doesNotMatch(syncPageSource, /\.calendarId/);

// Also ensure old enterprise-style metrics cards are not present
assert.doesNotMatch(syncPageSource, /令牌健康/);
assert.doesNotMatch(syncPageSource, /增量状态/);
assert.doesNotMatch(syncPageSource, /回写默认值/);
assert.doesNotMatch(syncPageSource, /冲突策略/);

// The old tenant/scope editor should be removed
assert.doesNotMatch(syncPageSource, /Tenant/);

// The old OutlookConflictResolver component should not be imported
assert.doesNotMatch(syncPageSource, /OutlookConflictResolver/);

// The old deviceCode should not be rendered in JSX
assert.doesNotMatch(syncPageSource, /deviceCodeMutation\.data\.deviceCode/);
assert.doesNotMatch(syncPageSource, /\.deviceCode\b/);

// --- New required patterns: remoteState usage ---
assert.match(syncPageSource, /remoteState/);
// Paused label
assert.match(syncPageSource, /暂停/);
// Remote-missing label
assert.match(syncPageSource, /缺失/);

// --- Read-only calendars must remain selectable ---
// The toggleBinding function should not filter by canEdit
assert.match(syncPageSource, /toggleBinding/);

// --- Auto-polling ---
// Must use setTimeout (serial polling), not setInterval for overlapping prevention
assert.match(syncPageSource, /setTimeout/);
// Must have independent refs: pollTimeoutRef and countdownIntervalRef
assert.match(syncPageSource, /pollTimeoutRef/);
assert.match(syncPageSource, /countdownIntervalRef/);
// scheduleNextPoll for serial scheduling
assert.match(syncPageSource, /scheduleNextPoll/);

// --- Task 8: poll error schedules next poll with same sessionId ---
assert.match(syncPageSource, /schedulePoll/);
assert.match(syncPageSource, /schedulePoll\(variables/);

// --- Task 8: poll success clears sessionError ---
assert.match(syncPageSource, /const pollMutation[\s\S]{0,2000}setSessionError\(null\)/);

// --- Task 8: disconnect clears local preview state ---
assert.match(syncPageSource, /disconnectMutation[\s\S]{0,2000}setShowLocalPreview\(false\)/);
assert.match(syncPageSource, /disconnectMutation[\s\S]{0,2000}setLocalPreviewData\(null\)/);
assert.match(syncPageSource, /disconnectMutation[\s\S]{0,2000}setLocalDeleteConfirm\(false\)/);

// --- Task 8: countdown effect uses stable boolean dependency ---
assert.match(syncPageSource, /countdownActive/);
assert.doesNotMatch(syncPageSource, /},\s*\[pollCountdown\]\)/);

// --- Cancel mutation ---
assert.match(syncPageSource, /cancelDeviceCodeMutation/);
assert.match(syncPageSource, /cancelDeviceCodeMutation\.isPending/);

// --- Check connection button ---
assert.match(syncPageSource, /检查连接/);

// --- lucide-react icons ---
assert.match(syncPageSource, /lucide-react/);

// --- lowercase statuses ---
assert.match(syncPageSource, /connected|waiting-for-user|starting/);

// --- perCalendarJson parsing ---
assert.match(syncPageSource, /perCalendarJson/);

// --- force-fetch-all uses full-resources mode via syncMutation ---
assert.match(syncPageSource, /full-resources/);
assert.match(syncPageSource, /handleForceFetchAll/);
assert.match(syncPageSource, /syncMutation\.mutate/);

// --- UUID pattern on Client ID input ---
assert.match(syncPageSource, /pattern="\^\[/);
assert.match(syncPageSource, /const \[showSetupGuide, setShowSetupGuide\] = useState\(true\)/);
assert.match(syncPageSource, /pattern="\^\[[^\n]+\n\s+required/);
assert.match(syncPageSource, /const localDataPreviewMutation = useMutation\(\{[\s\S]+mutationFn: outlookLocalDataPreview/);
assert.doesNotMatch(syncPageSource, /function handleLocalDataPreview\(\)[\s\S]{0,300}\.then\(/);
assert.doesNotMatch(
  syncPageSource,
  /setQueryData\(\['outlook-settings'\], data\);\s+queryClient\.invalidateQueries\(\{ queryKey: \['outlook-settings'\] \}\)/,
);
assert.ok(
  (syncPageSource.match(/\['workbench-outlook-settings'\]/g) ?? []).length >= 3,
  'settings save and authorization success should refresh Workbench settings',
);

// --- selectionMutation onSuccess must consume bindings ---
// Must setBindings and setSelectedBindingIds from the returned data
assert.match(syncPageSource, /onSuccess.*data/);

// --- checkConnectionMutation onSuccess must update query data ---
assert.match(syncPageSource, /setQueryData/);

// --- uiStatus usage with mapUiStatus ---
assert.match(syncPageSource, /uiStatus/);
assert.match(syncPageSource, /mapUiStatus/);
// Must map raw codes to Chinese labels
assert.match(syncPageSource, /not-configured/);
assert.match(syncPageSource, /waiting-auth/);
assert.match(syncPageSource, /reauth-required/);
// Must not expose tokenHealth/tenant/scopes in SyncPage UI
assert.doesNotMatch(syncPageSource, /tokenHealth/);
assert.doesNotMatch(syncPageSource, /\.scopes/);

// --- CalendarPage luxon3 and local-timezone contract ---
assert.match(calendarPageSource, /luxon3/);
assert.match(calendarPageSource, /@fullcalendar\/luxon3/);
assert.match(calendarPageSource, /luxon3Plugin/);
assert.match(calendarPageSource, /timeZone="local"/);
// Production must not hardcode a fixed timezone into the calendar board
assert.doesNotMatch(calendarPageSource, /timeZone="Asia\/Shanghai"/);
const pluginsLine = calendarPageSource.match(/plugins.*\[.*\]/s);
if (pluginsLine) {
  assert.match(pluginsLine[0], /luxon3Plugin/);
}

// --- Task 8: full-resources action must be described as a PR2 backfill ---
assert.match(syncPageSource, /刷新所有 Microsoft 日程并回填新支持字段/);
assert.match(syncPageSource, /深度同步：刷新所有 Microsoft 日程并回填新支持字段/);
assert.match(syncPageSource, /handleForceFetchAll/);
// The backfill must reuse the existing full-resources sync mode and endpoint
assert.match(syncPageSource, /mode: 'full-resources'/);
assert.doesNotMatch(syncPageSource, /backfill/);

// --- Type contract checks ---
// UpdateOutlookSettingsRequest must only have clientId
assert.match(typesSource, /interface UpdateOutlookSettingsRequest/);
const updateReqMatch = typesSource.match(/interface UpdateOutlookSettingsRequest\s*\{[^}]+\}/);
if (updateReqMatch) {
  const iface = updateReqMatch[0];
  assert.match(iface, /clientId/);
  assert.doesNotMatch(iface, /tenantId/);
  assert.doesNotMatch(iface, /scopes/);
}

// OutlookAuthorizationSessionResponse should exist
assert.match(typesSource, /OutlookAuthorizationSessionResponse/);
const sessionMatch = typesSource.match(/interface OutlookAuthorizationSessionResponse\s*\{[^}]+\}/);
if (sessionMatch) {
  const iface = sessionMatch[0];
  assert.match(iface, /verificationUri/);
  assert.match(iface, /userCode/);
  assert.match(iface, /expiresAt/);
  assert.match(iface, /accountDisplayName/);
  assert.match(iface, /recoveryAction/);
}

// Old OutlookSettingsResponse should have extended fields with uiStatus
assert.match(typesSource, /interface OutlookSettingsResponse/);
const settingsMatch = typesSource.match(/interface OutlookSettingsResponse\s*\{[^}]+\}/);
if (settingsMatch) {
  const iface = settingsMatch[0];
  assert.doesNotMatch(iface, /deltaLink/);
  assert.doesNotMatch(iface, /syncWindowDays/);
  assert.doesNotMatch(iface, /writebackDefault/);
  assert.doesNotMatch(iface, /conflictPolicy/);
  assert.match(iface, /uiStatus/);
  assert.match(iface, /activeAuthorization/);
  assert.match(iface, /tenantId/);
  assert.match(iface, /scopes/);
}

// OutlookCalendarBindingResponse must have pimCalendarId, graphCalendarId, remoteState, isSelected
// Must NOT have calendarId, isPaused, remoteMissing
assert.match(typesSource, /OutlookCalendarBindingResponse/);
const bindingMatch = typesSource.match(/interface OutlookCalendarBindingResponse\s*\{[^}]+\}/);
if (bindingMatch) {
  const iface = bindingMatch[0];
  assert.match(iface, /pimCalendarId/);
  assert.match(iface, /graphCalendarId/);
  assert.match(iface, /remoteState/);
  assert.match(iface, /isSelected/);
  assert.match(iface, /groupName/);
  assert.match(iface, /canEdit/);
  assert.match(iface, /ownerName/);
  assert.doesNotMatch(iface, /calendarId\b/);
  assert.doesNotMatch(iface, /isPaused/);
  assert.doesNotMatch(iface, /remoteMissing/);
}

// OutlookSyncRequest must NOT have failedCalendarBindingIds
assert.match(typesSource, /OutlookSyncRequest/);
const syncReqMatch = typesSource.match(/interface OutlookSyncRequest\s*\{[^}]+\}/);
if (syncReqMatch) {
  const iface = syncReqMatch[0];
  assert.match(iface, /mode/);
  assert.match(iface, /full-resources/);
  assert.match(iface, /range-instances/);
  assert.match(iface, /retryOfBatchId/);
  assert.match(iface, /rangeStart/);
  assert.match(iface, /rangeEnd/);
  assert.match(iface, /calendarBindingIds/);
  assert.doesNotMatch(iface, /failedCalendarBindingIds/);
}

// OutlookSyncBatchResponse must have perCalendarJson, cancelRequested, NO page/pageSize/totalCount/failedCalendarBindings
assert.match(typesSource, /OutlookSyncBatchResponse/);
const batchMatch = typesSource.match(/interface OutlookSyncBatchResponse\s*\{[^}]+\}/);
if (batchMatch) {
  const iface = batchMatch[0];
  assert.match(iface, /mode/);
  assert.match(iface, /perCalendarJson/);
  assert.match(iface, /cancelRequested/);
  assert.match(iface, /requestedWindowStart/);
  assert.match(iface, /requestedWindowEnd/);
  assert.doesNotMatch(iface, /page\b/);
  assert.doesNotMatch(iface, /pageSize/);
  assert.doesNotMatch(iface, /totalCount/);
  assert.doesNotMatch(iface, /failedCalendarBindings/);
}

// OutlookSyncBatchPage must exist
assert.match(typesSource, /OutlookSyncBatchPage/);

// OutlookPerCalendarResult must exist
assert.match(typesSource, /OutlookPerCalendarResult/);
assert.match(typesSource, /OutlookPerCalendarFailure/);

// OutlookLocalDataPreview must have bindingCount, calendarCount, eventCount
assert.match(typesSource, /OutlookLocalDataPreview/);
const localMatch = typesSource.match(/interface OutlookLocalDataPreview\s*\{[^}]+\}/);
if (localMatch) {
  const iface = localMatch[0];
  assert.match(iface, /bindingCount/);
  assert.match(iface, /calendarCount/);
  assert.match(iface, /eventCount/);
}

// --- API exports contract ---
assert.match(calendarApiSource, /outlookDiscover\(\)/);
assert.match(calendarApiSource, /outlookSelection\(\)/);
assert.match(calendarApiSource, /outlookWriteback\(\)/);
assert.match(calendarApiSource, /outlookLocalDataPreview\(\)/);
assert.match(calendarApiSource, /outlookLocalData\(\)/);
assert.match(calendarApiSource, /outlookDisconnect\(\)/);
assert.match(calendarApiSource, /outlookSyncCancel\(/);
assert.match(calendarApiSource, /outlookDeviceCodeCancel\(/);
assert.match(calendarApiSource, /outlookCheck\(\)/);

// Exported functions
assert.match(calendarApiSource, /export async function outlookDiscover/);
assert.match(calendarApiSource, /export async function outlookSelection/);
assert.match(calendarApiSource, /export async function outlookLocalDataPreview/);
assert.match(calendarApiSource, /export async function outlookLocalDataDelete/);
assert.match(calendarApiSource, /export async function outlookDisconnect/);
assert.match(calendarApiSource, /export async function cancelOutlookSync/);
assert.match(calendarApiSource, /export async function getOutlookSyncBatchesPaged/);
assert.match(
  calendarApiSource,
  /export async function getOutlookSyncBatches\(\)[\s\S]+getOutlookSyncBatchesPaged\(\)[\s\S]+return page\.items/,
);
assert.match(calendarApiSource, /export async function cancelOutlookDeviceCode/);
assert.match(calendarApiSource, /export async function checkOutlookConnection/);

// Only one runOutlookSync, no runOutlookSyncWithRequest
assert.match(calendarApiSource, /export async function runOutlookSync/);
assert.doesNotMatch(calendarApiSource, /runOutlookSyncWithRequest/);

// --- #272/#273/#309: binding state changes must be visible without a manual 发现日历 ---
// Automatic sync can change remote_state, and since #309 it can remove a calendar entirely
// (mirror-delete). Either way the page has to re-read the stored bindings afterwards,
// otherwise the「日历选择」list goes stale until the user clicks 发现日历.
assert.match(syncPageSource, /outlookBindings/);
assert.match(syncPageSource, /async function refreshBindings\(\)/);
assert.match(syncPageSource, /await outlookBindings\(\)[\s\S]{0,200}setBindings\(data\)/);
// Refresh is wired into both the sync mutation and the per-calendar retry mutation.
assert.equal(
  (syncPageSource.match(/void refreshBindings\(\);/g) ?? []).length,
  2,
  'refreshBindings should run after sync and after per-calendar retry',
);
// Since #309 a confirmed-missing calendar is mirror-deleted, so the sync history — not the
// binding list — is what explains the disappearance to the user.
assert.match(syncPageSource, /step\.status === 'mirror-deleted'/);
assert.match(syncPageSource, /移入回收站/);
assert.match(calendarApiSource, /export async function outlookBindings/);
assert.match(calendarApiSource, /outlookCalendars\(\)/);
