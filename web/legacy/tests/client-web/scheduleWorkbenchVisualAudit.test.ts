import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createRequire } from 'node:module';
import { createServer } from 'node:net';
import { fileURLToPath } from 'node:url';
import { inflateSync } from 'node:zlib';

const requireFromWeb = createRequire(new URL('../../src/client-web/package.json', import.meta.url));
const { chromium } = requireFromWeb('playwright') as typeof import('playwright');
type Browser = import('playwright').Browser;
type Page = import('playwright').Page;

const routes = [
  '/today', '/calendar', '/tasks', '/habits', '/reminders', '/reports',
  '/settings/sync', '/data-center', '/confirmations',
  '/audit/task/00000000-0000-0000-0000-000000000001', '/endpoint-shell',
] as const;

const viewports = [[390, 844], [768, 1024], [1440, 1000]] as const;
const DEFAULT_TIMEZONE_ID = 'Asia/Shanghai';

// Mock fixtures in this suite use fixed 2026-07 dates, so the browser clock must
// be pinned to July 2026 instead of the real current date; otherwise the calendar
// month view shows a different month and every `.fc-event` lookup times out
// (regression: CI started failing on 2026-08-01 with no web changes).
const FIXED_TEST_TIME = new Date('2026-07-14T00:00:00.000Z');

async function newFixedContext(browser: Browser, options: Parameters<Browser['newContext']>[0]) {
  const context = await browser.newContext(options);
  await context.clock.setFixedTime(FIXED_TEST_TIME);
  return context;
}

const forbiddenHeadings = new Set([
  'Endpoint Shell', 'Collection Quality', 'Notification Action',
  'Confirmations Page', 'Reports Page',
]);

const SCREENSHOT_DIR = '.opencode-prompts/task9-screenshots';
const CAPTURE_SCREENSHOTS = process.env.PIM_CAPTURE_TASK9_SCREENSHOTS === '1';

interface CapturedRequest {
  url: string;
  method: string;
  body?: unknown;
}

async function main() {
  assertMobileCalendarHeightFallback();
  assertTaskEditorUsesAtomicUpdate();
  const port = await freePort();
  const baseUrl = `http://127.0.0.1:${port}`;
  const server = startVite(port);
  let browser: Browser | undefined;
  try {
    await waitForServer(baseUrl);
    browser = await chromium.launch({ headless: true });
    await runRouteAudit(browser, baseUrl);
    await runScenarioA(browser, baseUrl);
    await runScenarioB(browser, baseUrl);
    await runScenarioC(browser, baseUrl);
    await runScenarioD(browser, baseUrl);
    await runScenarioE(browser, baseUrl);
    await runScenarioF(browser, baseUrl);
    await runScenarioG(browser, baseUrl);
    await runScenarioH(browser, baseUrl);
    await runScenarioI(browser, baseUrl);
    await runScenarioJ(browser, baseUrl);
    await runScenarioK(browser, baseUrl);
    await runScenarioL(browser, baseUrl);
    await runScenarioM(browser, baseUrl);
    await runScenarioN(browser, baseUrl);
  } finally {
    await browser?.close();
    stopServer(server);
  }
}

// ─── Route audit ─────────────────────────────────────────────────────

async function runRouteAudit(browser: Browser, baseUrl: string) {
  for (const [width, height] of viewports) {
    const context = await newFixedContext(browser, {
      viewport: { width, height },
      timezoneId: DEFAULT_TIMEZONE_ID,
    });
    try {
      await context.addInitScript(() => {
        localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
      });
      await context.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const fullPath = url.pathname + url.search;
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify(mockApiResponse(fullPath)),
        });
      });
      await context.route('**/api/version', route => route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ version: '2026.08.100', capabilities: [], latestVersion: null, checkedAt: null, error: null }),
      }));
      await context.route('**/api/client/shell/latest', route => route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ windowsVersion: null, windowsUrl: null, androidVersion: null, androidUrl: null, checkedAt: null, error: null }),
      }));
      for (const route of routes) {
        const page = await context.newPage();
        const consoleErrors: string[] = [];
        page.on('console', message => {
          if (message.type() === 'error') consoleErrors.push(message.text());
        });
        await page.goto(`${baseUrl}${route}`, { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle', { timeout: 4_000 }).catch(() => undefined);
        await page.waitForFunction(
          () => (document.querySelector('main')?.textContent ?? '').trim().length > 0,
          null, { timeout: 6_000 },
        );
        await assertRoute(page, route, width, height);
        assert.deepEqual(consoleErrors, [],
          `${route} at ${width}x${height} should not log browser errors`);
        if (route === '/calendar' && width === 390) {
          await page.waitForSelector('.calendar-board', { state: 'visible', timeout: 4_000 });
          await page.waitForSelector('.calendar-board .fc', { state: 'visible', timeout: 4_000 });
          const calendarHeights = await page.evaluate(() => {
            const board = document.querySelector('.calendar-board');
            const calendar = board?.querySelector('.fc');
            return {
              board: board?.getBoundingClientRect().height ?? -1,
              calendar: calendar?.getBoundingClientRect().height ?? -1,
            };
          });
          assert.ok(calendarHeights.board >= 360,
            `390x844 calendar board height ${calendarHeights.board}px must be >= 360px`);
          assert.ok(calendarHeights.calendar >= 360,
            `390x844 FullCalendar height ${calendarHeights.calendar}px must be >= 360px`);
        }
        await page.close();
      }
    } finally {
      await context.close();
    }
  }
}

// ─── Scenario F: Missing ETag ────────────────────────────────────────

async function runScenarioF(browser: Browser, baseUrl: string) {
  const [w, h] = viewports[2];
  const context = await newFixedContext(browser, {
    viewport: { width: w, height: h },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  try {
    const captured: CapturedRequest[] = [];
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
    });
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      const method = route.request().method();
      if (method !== 'GET') {
        const postBody = route.request().postDataJSON();
        captured.push({ url: fullPath, method, body: postBody });
      }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, method)),
      });
    });
    await context.route('**/api/v1/calendar/outlook/events/writeback', async route => {
      const body = route.request().postDataJSON();
      captured.push({ url: 'writeback', method: 'POST', body });
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ code: 0, message: 'OK',
          data: { status: 'updated' }, timestamp: new Date().toISOString() }),
      });
    });

    const page = await context.newPage();
    await openCalendarMonth(page, baseUrl);

    // F1: No-ETag event — save blocks with Chinese error
    await openEventByText(page, 'Outlook 无版本事件');
    const titleInput = page.locator('aside[role="dialog"] input[type="text"]').first();
    await titleInput.fill('无版本事件修改');
    await page.locator('aside[role="dialog"] button[type="submit"]').click();

    const hasError = await page.locator('text=缺少版本标识').isVisible({ timeout: 3_000 }).catch(() => false);
    assert.ok(hasError, 'Missing ETag must show Chinese error message');
    const hasPreview = await page.locator('text=Outlook 写回 确认').isVisible({ timeout: 2_000 }).catch(() => false);
    assert.ok(!hasPreview, 'Writeback preview must not appear for missing ETag update');

    const preDeleteWb = captured.filter(c => c.url === 'writeback').length;

    // F2: Also click delete — same blocking
    const deleteBtn = page.locator('aside[role="dialog"] button', { hasText: '删除' });
    await deleteBtn.waitFor({ state: 'visible', timeout: 3_000 });
    await deleteBtn.click();
    const hasErrorDel = await page.locator('text=缺少版本标识').isVisible({ timeout: 3_000 }).catch(() => false);
    assert.ok(hasErrorDel, 'Missing ETag delete must also show error');
    const hasPreviewDel = await page.locator('text=Outlook 写回 确认').isVisible({ timeout: 2_000 }).catch(() => false);
    assert.ok(!hasPreviewDel, 'Writeback preview must not appear for missing ETag delete');

    assert.equal(captured.filter(c => c.url === 'writeback').length, preDeleteWb,
      'Zero writeback requests for missing ETag');

    await page.close();
  } finally {
    await context.close();
  }
}

// ─── Scenario G: Accessibility / pending ──────────────────────────────

async function runScenarioG(browser: Browser, baseUrl: string) {
  const [w, h] = viewports[2];
  const context = await newFixedContext(browser, {
    viewport: { width: w, height: h },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  let accessPage: Page | undefined;
  try {
    const captured: CapturedRequest[] = [];
    let holdNextWrite = false;
    let releasePending: (() => void) | undefined;
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
    });
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      const method = route.request().method();
      if (method !== 'GET') {
        const postBody = route.request().postDataJSON();
        captured.push({ url: fullPath, method, body: postBody });
      }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, method)),
      });
    });
    await context.route('**/api/v1/calendar/outlook/events/writeback', async route => {
      const body = route.request().postDataJSON();
      captured.push({ url: 'writeback', method: 'POST', body });
      if (holdNextWrite) {
        await new Promise<void>(r => { releasePending = r; });
      }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ code: 0, message: 'OK',
          data: { status: 'updated' }, timestamp: new Date().toISOString() }),
      });
    });

    accessPage = await context.newPage();
    await openCalendarMonth(accessPage, baseUrl);
    await openEventByText(accessPage, 'Outlook 可编辑事件');

    // Edit and save to enter preview
    await accessPage.locator('aside[role="dialog"] input[type="text"]').first().fill('可访问性测试');
    await accessPage.locator('aside[role="dialog"] button[type="submit"]').click();
    await waitForWritebackPreview(accessPage);

    // G1: Escape closes writeback dialog, editor stays open
    await accessPage.keyboard.press('Escape');
    const editorAfterEsc = await accessPage.locator('aside[role="dialog"]').isVisible({ timeout: 3_000 }).catch(() => false);
    assert.ok(editorAfterEsc, 'Editor must stay open after Escape closes writeback dialog');
    const wbAfterEsc = await accessPage.locator('text=Outlook 写回 确认').isVisible({ timeout: 1_000 }).catch(() => false);
    assert.ok(!wbAfterEsc, 'Writeback dialog must close after Escape');

    // No writeback request sent
    assert.equal(captured.filter(c => c.url === 'writeback').length, 0, 'No request after Escape');

    // G2: Reopen preview and test Tab focus trap
    await accessPage.locator('aside[role="dialog"] button[type="submit"]').click();
    await waitForWritebackPreview(accessPage);

    // Tab should cycle within writeback dialog
    const focusedInDialog = await accessPage.evaluate((sel) => {
      const dialog = document.querySelector(sel);
      if (!dialog) return false;
      const active = document.activeElement;
      return dialog.contains(active);
    }, 'div[role="dialog"][aria-modal="true"]');
    assert.ok(focusedInDialog, 'Focus must be inside writeback dialog');

    // Tab once
    await accessPage.keyboard.press('Tab');
    const stillInDialog = await accessPage.evaluate((sel) => {
      const dialog = document.querySelector(sel);
      if (!dialog) return false;
      return dialog.contains(document.activeElement);
    }, 'div[role="dialog"][aria-modal="true"]');
    assert.ok(stillInDialog, 'Tab must keep focus inside dialog');

    // Shift+Tab
    await accessPage.keyboard.press('Shift+Tab');
    const stillInDialogShift = await accessPage.evaluate((sel) => {
      const dialog = document.querySelector(sel);
      if (!dialog) return false;
      return dialog.contains(document.activeElement);
    }, 'div[role="dialog"][aria-modal="true"]');
    assert.ok(stillInDialogShift, 'Shift+Tab must keep focus inside dialog');

    // G3: Pending state — hold response behind gate
    // Close current dialog first
    await accessPage.keyboard.press('Escape');
    await accessPage.waitForFunction(
      () => !document.querySelector('div[role="dialog"][aria-modal="true"]'),
      null, { timeout: 3_000 },
    ).catch(() => undefined);

    // Re-open, re-enter preview
    await accessPage.locator('aside[role="dialog"] button[type="submit"]').click();
    await waitForWritebackPreview(accessPage);

    // Set up gate — flag BEFORE click so the route handler sees it race-free
    holdNextWrite = true;
    await writebackConfirmBtn(accessPage).click();

    // Wait for the route handler to capture the request and block
    for (let attempt = 0; attempt < 100; attempt++) {
      if (releasePending !== undefined) break;
      await new Promise(r => setTimeout(r, 50));
    }
    assert.ok(releasePending !== undefined, 'Route handler must be blocked by gate before disabled check');

    // While pending, confirm/cancel are disabled and Escape does nothing
    const confirmDisabled = await writebackConfirmBtn(accessPage).isDisabled().catch(() => true);
    const cancelDisabled = await accessPage.locator('div[role="dialog"][aria-modal="true"]').locator('button', { hasText: '取消' }).isDisabled().catch(() => true);
    assert.ok(confirmDisabled, 'Confirm must be disabled while submitting');
    assert.ok(cancelDisabled, 'Cancel must be disabled while submitting');

    // Escape should not close during submit
    await accessPage.keyboard.press('Escape');
    const wbStillUp = await accessPage.locator('div[role="dialog"][aria-modal="true"]').isVisible({ timeout: 2_000 }).catch(() => false);
    assert.ok(wbStillUp, 'Writeback dialog must stay open during submit');

    // Release the gate
    releasePending!();
    holdNextWrite = false;
    releasePending = undefined;

    // Wait for dialog to close
    await waitForNoWritebackDialog(accessPage);
    assert.equal(captured.filter(c => c.url === 'writeback').length, 1, 'Writeback request sent after release');

    await accessPage.close();
    accessPage = undefined;
  } finally {
    if (accessPage) await accessPage.close().catch(() => undefined);
    await context.close();
  }
}

// ─── Scenario H: Three viewports ──────────────────────────────────────

async function runScenarioH(browser: Browser, baseUrl: string) {
  if (CAPTURE_SCREENSHOTS) mkdirSync(SCREENSHOT_DIR, { recursive: true });

  for (const [width, height] of viewports) {
    const context = await newFixedContext(browser, {
      viewport: { width, height },
      timezoneId: DEFAULT_TIMEZONE_ID,
    });
    try {
      const captured: CapturedRequest[] = [];
      let conflictSeq = 0;
      await context.addInitScript(() => {
        localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
      });
      await context.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const fullPath = url.pathname + url.search;
        const method = route.request().method();
        if (method !== 'GET') {
          const postBody = route.request().postDataJSON();
          captured.push({ url: fullPath, method, body: postBody });
        }
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify(mockApiResponse(fullPath, method)),
        });
      });
      await context.route('**/api/v1/calendar/outlook/events/writeback', async route => {
        const body = route.request().postDataJSON();
        captured.push({ url: 'writeback', method: 'POST', body });
        conflictSeq++;
        if (conflictSeq === 1) {
          return route.fulfill({
            status: 409, contentType: 'application/json',
            body: JSON.stringify(conflictResponse('Outlook Updated Title', 'etag-new-001')),
          });
        }
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify({ code: 0, message: 'OK',
            data: { status: 'updated' }, timestamp: new Date().toISOString() }),
        });
      });

      const page = await context.newPage();
      await openCalendarMonth(page, baseUrl);

      // H1: Open event editor, assert dialog in viewport
      await openEventByText(page, 'Outlook 可编辑事件', true);
      await assertDialogInViewport(page, 'aside[role="dialog"]');

      // H2: Open writeback preview
      await page.locator('aside[role="dialog"] input[type="text"]').first().fill(`VP-${width}`);
      await page.locator('aside[role="dialog"] button[type="submit"]').click();
      await waitForWritebackPreview(page);
      await assertDialogInViewport(page, 'div[role="dialog"][aria-modal="true"]');

      // H3: Open conflict state
      await writebackConfirmBtn(page).click();
      await waitForConflict(page);
      await assertDialogInViewport(page, 'div[role="dialog"][aria-modal="true"]');
      const conflictCancelBox = await page
        .locator('div[role="dialog"][aria-modal="true"] button', { hasText: '取消' })
        .boundingBox();
      assert.ok(conflictCancelBox && conflictCancelBox.height <= 48,
        'Conflict cancel command must remain on one line');

      // H4: Conflict shows the typed latest event, not raw JSON
      assert.equal(
        await page.locator('div[role="dialog"][aria-modal="true"] pre').count(),
        0,
        'Conflict must not render raw JSON at any viewport',
      );
      await latestOutlookBlock(page)
        .getByText('Outlook Updated Title')
        .waitFor({ state: 'visible', timeout: 3_000 });

      if (CAPTURE_SCREENSHOTS) {
        const tag = width === 390 ? '390' : width === 768 ? '768' : '1440';
        const buf = await page.screenshot({ fullPage: false });
        writeFileSync(`${SCREENSHOT_DIR}/${tag}-conflict.png`, buf);
      }

      await page.close();
    } finally {
      await context.close();
    }
  }

  // Preview screenshots at specific viewports
  if (CAPTURE_SCREENSHOTS) {
    for (const [w, h] of [[390, 844], [1440, 1000]] as const) {
      const context = await newFixedContext(browser, {
        viewport: { width: w, height: h },
        timezoneId: DEFAULT_TIMEZONE_ID,
      });
      try {
        await context.addInitScript(() => {
          localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
        });
        await context.route('**/api/v1/**', route => route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify(mockApiResponse(new URL(route.request().url()).pathname + new URL(route.request().url()).search)),
        }));
        const page = await context.newPage();
        await openCalendarMonth(page, baseUrl);
        await openEventByText(page, 'Outlook 可编辑事件', true);
        await page.locator('aside[role="dialog"] input[type="text"]').first().fill(`VP-${w}`);
        await page.locator('aside[role="dialog"] button[type="submit"]').click();
        await waitForWritebackPreview(page);

        const tag = w === 390 ? '390' : '1440';
        const buf = await page.screenshot({ fullPage: false });
        writeFileSync(`${SCREENSHOT_DIR}/${tag}-preview.png`, buf);

        await page.close();
      } finally {
        await context.close();
      }
    }
  }
}

// ─── Scenario I: Time validation, calendar selection, HTML description ─

async function runScenarioI(browser: Browser, baseUrl: string) {
  const [w, h] = viewports[2];
  const context = await newFixedContext(browser, {
    viewport: { width: w, height: h },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  try {
    const captured: CapturedRequest[] = [];
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
      (window as any).__pimHtmlExecuted = false;
    });
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      const method = route.request().method();
      if (method !== 'GET') {
        const postBody = route.request().postDataJSON();
        captured.push({ url: fullPath, method, body: postBody });
      }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, method)),
      });
    });

    const page = await context.newPage();
    await openCalendarMonth(page, baseUrl);

    // ── Part A: Manual event with timezone ──────────────────────

    await openEventByText(page, '手动创建的事件');

    const dtInputs = page.locator('aside[role="dialog"] input[type="datetime-local"]');
    const startVal = await dtInputs.first().inputValue();
    const endVal = await dtInputs.nth(1).inputValue();
    assert.ok(startVal.length > 0, 'Start datetime-local must be non-empty');
    assert.ok(endVal.length > 0, 'End datetime-local must be non-empty');
    assert.ok(!startVal.includes('+') && !startVal.includes('Z'), 'Start must not contain UTC offset');
    assert.ok(!endVal.includes('+') && !endVal.includes('Z'), 'End must not contain UTC offset');
    assert.equal(startVal, '2026-07-14T14:00', 'Start must be 2026-07-14T14:00');

    // End min attribute uses minimumEndValue
    const minAttr = await dtInputs.nth(1).getAttribute('min');
    assert.equal(minAttr, '2026-07-14T14:01', 'End input min must use minimumEndValue');

    // Set end equal to start, submit, assert Chinese range error and no POST/PUT
    await dtInputs.nth(1).fill(startVal);
    const reqBefore = captured.filter(c => c.method === 'POST' || c.method === 'PUT').length;
    await page.locator('aside[role="dialog"] button[type="submit"]').click();
    await page.waitForTimeout(500);
    const rangeError = await page.locator('text=结束时间必须晚于开始时间').isVisible({ timeout: 3_000 }).catch(() => false);
    assert.ok(rangeError, 'End <= start must show Chinese validation error');
    const reqAfter = captured.filter(c => c.method === 'POST' || c.method === 'PUT').length;
    assert.equal(reqAfter, reqBefore, 'No request sent when end equals start');

    // Close and reopen
    await page.locator('aside[role="dialog"] footer button', { hasText: '取消' }).click();
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 3_000 }).catch(() => undefined);
    await openEventByText(page, '手动创建的事件');

    // Edit title and submit
    const titleInput = page.locator('aside[role="dialog"] input[type="text"]').first();
    await titleInput.fill('时区手动事件');
    await page.locator('aside[role="dialog"] button[type="submit"]').click();

    await page.waitForFunction(
      () => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 5_000 },
    ).catch(() => undefined);

    // Assert exactly one relevant PUT
    const putCalls = captured.filter(c => c.method === 'PUT' && c.url.includes('evt-manual-1'));
    assert.equal(putCalls.length, 1, 'Exactly one PUT for evt-manual-1');
    const putBody = putCalls[0].body as Record<string, unknown>;
    assert.ok(
      typeof putBody.dtStart === 'string' && putBody.dtStart.includes('T06:00:00') && putBody.dtStart.endsWith('Z'),
      'dtStart must be T06:00:00.000Z UTC in PUT body',
    );
    assert.ok(
      typeof putBody.dtEnd === 'string' && putBody.dtEnd.endsWith('Z'),
      'dtEnd must be UTC ISO in PUT body',
    );
    assert.equal(putBody.calendarId, 'cal-manual-1', 'PUT must use cal-manual-1');

    // Close editor if still open
    await page.locator('aside[role="dialog"] footer button', { hasText: '取消' }).click().catch(() => undefined);
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 3_000 }).catch(() => undefined);

    // ── Part B: New event with blank-title validation ────────────

    const inboxPanel = page.getByRole('heading', { name: '收集箱', exact: true }).locator('../..');
    await inboxPanel.getByRole('button', { name: '+ 新建', exact: true }).click();
    await inboxPanel.getByRole('button', { name: '日程', exact: true }).click();
    await page.locator('aside[role="dialog"] h2', { hasText: '新建日程' }).waitFor({ state: 'visible', timeout: 5_000 });

    const calSelect = page.locator('aside[role="dialog"] select').first();
    await calSelect.locator('option[value="cal-manual-1"]').waitFor({ state: 'attached', timeout: 3_000 });
    const selectedCal = await calSelect.inputValue();
    assert.equal(selectedCal, 'cal-manual-1', 'New event defaults to cal-manual-1');

    // Calendar select must have no empty placeholder option
    const optionValues: string[] = await calSelect.evaluate(
      (sel: HTMLSelectElement) => Array.from(sel.options).map(o => o.value)
    );
    assert.ok(!optionValues.includes(''), 'Calendar select must not have empty value="" placeholder option');

    // Outlook calendar option must include (Outlook) suffix
    const outlookOptionText = await calSelect.locator('option[value="cal-outlook-1"]').textContent();
    assert.ok(outlookOptionText?.includes('(Outlook)'), 'Outlook calendar option must show (Outlook) suffix');

    // Blank title path — submit without filling title, assert error and no request
    const reqBefore2 = captured.filter(c => c.method === 'POST' || c.method === 'PUT').length;
    await page.locator('aside[role="dialog"] button[type="submit"]').click();
    const blankTitleAlert = page.getByRole('alert').filter({ hasText: '请输入标题' });
    await blankTitleAlert.waitFor({ state: 'visible', timeout: 3_000 });
    const reqAfter2 = captured.filter(c => c.method === 'POST' || c.method === 'PUT').length;
    assert.equal(reqAfter2, reqBefore2, 'No request sent when title is blank');

    // Missing time path — fill title but leave datetime-local fields empty
    const missingTitleInput = page.locator('aside[role="dialog"] input[type="text"]').first();
    await missingTitleInput.fill('缺少时间测试');
    const missingDtInputs = page.locator('aside[role="dialog"] input[type="datetime-local"]');
    await missingDtInputs.first().fill('');
    await missingDtInputs.nth(1).fill('');
    assert.equal(await missingDtInputs.first().inputValue(), '', 'Start datetime-local must be empty after clear');
    assert.equal(await missingDtInputs.nth(1).inputValue(), '', 'End datetime-local must be empty after clear');
    const reqBefore3 = captured.filter(c => c.method === 'POST' || c.method === 'PUT').length;
    await page.locator('aside[role="dialog"] button[type="submit"]').click();
    const missingTimeAlert = page.getByRole('alert').filter({ hasText: '请选择开始和结束时间' });
    await missingTimeAlert.waitFor({ state: 'visible', timeout: 3_000 });
    const reqAfter3 = captured.filter(c => c.method === 'POST' || c.method === 'PUT').length;
    assert.equal(reqAfter3, reqBefore3, 'No request sent when start/end are empty');

    // Now fill title and valid times, submit
    const newTitleInput = page.locator('aside[role="dialog"] input[type="text"]').first();
    await newTitleInput.fill('默认日历新建事件');
    const newDtInputs = page.locator('aside[role="dialog"] input[type="datetime-local"]');
    await newDtInputs.first().fill('2026-07-21T14:00');
    await newDtInputs.nth(1).fill('2026-07-21T15:00');
    await page.locator('aside[role="dialog"] button[type="submit"]').click();

    await page.waitForFunction(
      () => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 5_000 },
    ).catch(() => undefined);

    const postCalls = captured.filter(c => c.method === 'POST' && c.url.includes('/calendar/events'));
    assert.ok(postCalls.length >= 1, 'POST to /calendar/events for new event');
    const postBody = postCalls[postCalls.length - 1].body as Record<string, unknown>;
    assert.equal(postBody.title, '默认日历新建事件');
    assert.equal(postBody.calendarId, 'cal-manual-1');
    assert.ok(
      typeof postBody.dtStart === 'string' && postBody.dtStart.endsWith('.000Z'),
      'dtStart must be UTC ISO in POST body',
    );
    assert.ok(
      typeof postBody.dtEnd === 'string' && postBody.dtEnd.endsWith('.000Z'),
      'dtEnd must be UTC ISO in POST body',
    );

    // Close editor if still open
    await page.locator('aside[role="dialog"] footer button', { hasText: '取消' }).click().catch(() => undefined);
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 3_000 }).catch(() => undefined);

    // ── Part C: HTML description with sanitization ───────────────

    await openEventByText(page, 'HTML 描述事件');

    const previewEl = page.locator('[data-description-html-preview]');
    await previewEl.waitFor({ state: 'visible', timeout: 3_000 });

    // Formatted text visible
    const previewText = await previewEl.textContent();
    assert.ok(previewText.includes('HTML'), 'Formatted text visible in preview');

    // Raw <div and <script not shown as visible text
    assert.ok(!previewText.includes('<div'), 'Raw <div not shown as text');
    assert.ok(!previewText.includes('<script'), 'Raw <script not shown as text');

    // No visible textarea
    const textareaVisible = await page.locator('aside[role="dialog"] textarea').isVisible({ timeout: 1_000 }).catch(() => false);
    assert.ok(!textareaVisible, 'Textarea must not be visible when HTML preview shown');

    // Script and onerror not executed
    const htmlExecuted = await page.evaluate(() => (window as unknown as Record<string, unknown>).__pimHtmlExecuted);
    assert.equal(htmlExecuted, false, 'Script/onerror must not have executed');

    // ── Part D: No writable calendars ──────────────────────
    {
      const noCtx = await newFixedContext(browser, {
        viewport: { width: w, height: h },
        timezoneId: DEFAULT_TIMEZONE_ID,
      });
      try {
        const noCap: CapturedRequest[] = [];
        await noCtx.addInitScript(() => {
          localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
        });
        await noCtx.route('**/api/v1/**', route => {
          const url = new URL(route.request().url());
          const fullPath = url.pathname + url.search;
          const method = route.request().method();
          if (method !== 'GET') {
            const postBody = route.request().postDataJSON();
            noCap.push({ url: fullPath, method, body: postBody });
          }
          const response = mockApiResponse(fullPath, method);
          if (fullPath.includes('/calendar/calendars') && !fullPath.includes('outlook')) {
            response.data = [
              { id: 'cal-ro-1', name: '只读日历 A', color: '#888888', kind: 'calendar', isDefault: true, canEdit: false },
              { id: 'cal-ro-2', name: '只读日历 B', color: '#999999', kind: 'calendar', isDefault: false, canEdit: false },
            ];
          }
          return route.fulfill({
            status: 200, contentType: 'application/json',
            body: JSON.stringify(response),
          });
        });

        const noPage = await noCtx.newPage();
        await openCalendarMonth(noPage, baseUrl);

        const inboxPanel = noPage.getByRole('heading', { name: '收集箱', exact: true }).locator('../..');
        await inboxPanel.getByRole('button', { name: '+ 新建', exact: true }).click();
        await inboxPanel.getByRole('button', { name: '日程', exact: true }).click();
        await noPage.locator('aside[role="dialog"] h2', { hasText: '新建日程' }).waitFor({ state: 'visible', timeout: 5_000 });

        const noWritableMessage = '没有可用的可写日历，请先在设置中添加或启用日历';
        const inlineWarning = noPage.locator('aside[role="dialog"] p').filter({ hasText: noWritableMessage });
        await inlineWarning.waitFor({ state: 'visible', timeout: 3_000 });
        const submitButton = noPage.locator('aside[role="dialog"] button[type="submit"]');
        assert.ok(await submitButton.isDisabled(), 'Create button must be disabled when no writable calendar exists');

        // Fill form with valid data and try to submit
        await noPage.locator('aside[role="dialog"] input[type="text"]').first().fill('无日历创建测试');
        const dtInputs = noPage.locator('aside[role="dialog"] input[type="datetime-local"]');
        await dtInputs.first().fill('2026-07-21T14:00');
        await dtInputs.nth(1).fill('2026-07-21T15:00');

        const reqBefore = noCap.filter(c => c.method === 'POST' || c.method === 'PUT').length;
        await noPage.locator('form#event-editor-form').evaluate(form => (form as HTMLFormElement).requestSubmit());
        const noWritableAlert = noPage.getByRole('alert').filter({ hasText: noWritableMessage });
        await noWritableAlert.waitFor({ state: 'visible', timeout: 3_000 });
        assert.equal(noCap.filter(c => c.method === 'POST' || c.method === 'PUT').length, reqBefore,
          'No POST/PUT request sent when no writable calendar');

        await noPage.close();
      } finally {
        await noCtx.close();
      }
    }

    // ── Part E: Calendar loading state ──────────────────────────
    {
      const loadCtx = await newFixedContext(browser, {
        viewport: { width: w, height: h },
        timezoneId: DEFAULT_TIMEZONE_ID,
      });
      let releaseCalendars!: () => void;
      const calendarsGate = new Promise<void>(resolve => {
        releaseCalendars = resolve;
      });
      try {
        let calendarsBlocked = false;
        await loadCtx.addInitScript(() => {
          localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
        });
        await loadCtx.route('**/api/v1/**', async route => {
          const url = new URL(route.request().url());
          const fullPath = url.pathname + url.search;
          const method = route.request().method();
          if (fullPath.includes('/calendar/calendars') && !fullPath.includes('outlook')) {
            calendarsBlocked = true;
            await calendarsGate;
          }
          return route.fulfill({
            status: 200, contentType: 'application/json',
            body: JSON.stringify(mockApiResponse(fullPath, method)),
          });
        });

        const loadPage = await loadCtx.newPage();
        await loadPage.goto(`${baseUrl}/calendar?view=month`, { waitUntil: 'domcontentloaded' });

        // Open the new-event dialog
        const inboxPanel = loadPage.getByRole('heading', { name: '收集箱', exact: true }).locator('../..');
        await inboxPanel.getByRole('button', { name: '+ 新建', exact: true }).waitFor({ state: 'visible', timeout: 5_000 });
        await inboxPanel.getByRole('button', { name: '+ 新建', exact: true }).click();
        await inboxPanel.getByRole('button', { name: '日程', exact: true }).waitFor({ state: 'visible', timeout: 3_000 });
        await inboxPanel.getByRole('button', { name: '日程', exact: true }).click();
        await loadPage.locator('aside[role="dialog"] h2', { hasText: '新建日程' }).waitFor({ state: 'visible', timeout: 5_000 });

        // Wait for the route handler to have trapped the calendars request
        for (let attempt = 0; attempt < 100; attempt++) {
          if (calendarsBlocked) break;
          await new Promise(r => setTimeout(r, 50));
        }

        const calSelect = loadPage.locator('aside[role="dialog"] select').first();

        // E1: Loading state assertions
        const loadingOption = calSelect.locator('option[value=""]');
        assert.equal(await loadingOption.count(), 1, 'Loading state must expose one empty-value option');
        const loadingText = await loadingOption.textContent();
        assert.equal(loadingText, '正在加载日历...', 'Loading state must show disabled empty-value option with "正在加载日历..." text');
        assert.ok(await loadingOption.isDisabled(), 'Loading calendar option must be disabled');
        assert.ok(await calSelect.isDisabled(), 'Calendar select must be disabled while loading');
        assert.ok(await loadPage.locator('aside[role="dialog"] button[type="submit"]').isDisabled(),
          'Submit button must be disabled while loading');

        const noWritableWarning = '没有可用的可写日历，请先在设置中添加或启用日历';
        const warningShown = await loadPage.locator('aside[role="dialog"] p').filter({ hasText: noWritableWarning }).isVisible({ timeout: 1_000 }).catch(() => false);
        assert.ok(!warningShown, 'No-writable warning must not appear while loading');

        // Release the gate so calendars response arrives
        releaseCalendars();

        // E2: After-load assertions
        await calSelect.locator('option[value="cal-manual-1"]').waitFor({ state: 'attached', timeout: 5_000 });
        assert.equal(await calSelect.inputValue(), 'cal-manual-1', 'Default calendar after load must be cal-manual-1');

        const optionValues: string[] = await calSelect.evaluate(
          (sel: HTMLSelectElement) => Array.from(sel.options).map(o => o.value),
        );
        assert.ok(!optionValues.includes(''), 'No empty placeholder option must remain after load');
        assert.ok(await calSelect.isEnabled(), 'Calendar select must be enabled after load');

        const warningAfterLoad = await loadPage.locator('aside[role="dialog"] p').filter({ hasText: noWritableWarning }).isVisible({ timeout: 1_000 }).catch(() => false);
        assert.ok(!warningAfterLoad, 'No-writable warning must remain absent after load');

        await loadPage.close();
      } finally {
        releaseCalendars();
        await loadCtx.close();
      }
    }

    await page.close();
  } finally {
    await context.close();
  }
}

// ─── Scenario J: Task editor reliability ─────────────────────────────

async function runScenarioJ(browser: Browser, baseUrl: string) {
  const context = await newFixedContext(browser, {
    viewport: { width: 1440, height: 1000 },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  try {
    const captured: CapturedRequest[] = [];
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
    });
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      const method = route.request().method();
      if (method !== 'GET') {
        const postBody = route.request().postDataJSON();
        captured.push({ url: fullPath, method, body: postBody });
      }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, method)),
      });
    });

    const page = await context.newPage();
    await openCalendarMonth(page, baseUrl);

    // ── Part A: Existing scheduled-task edit ──────────────────────

    await openEventByText(page, '排程任务');

    // Assert number input controls: 时/分钟 (estimated duration) + 番茄钟 (minimum segment, #185 workorder)
    const numberInputs = page.locator('aside[role="dialog"] input[type="number"]');
    assert.equal(await numberInputs.count(), 3);

    // Locate by accessible labels 时 and 分钟
    const hourInput = page.locator('aside[role="dialog"]')
      .getByRole('spinbutton', { name: '时', exact: true });
    const minuteInput = page.locator('aside[role="dialog"]')
      .getByRole('spinbutton', { name: '分钟', exact: true });
    assert.equal(await hourInput.inputValue(), '1');
    assert.equal(await minuteInput.inputValue(), '30');

    // Assert three datetime-local values are non-empty and contain neither Z nor +
    const dtInputs = page.locator('aside[role="dialog"] input[type="datetime-local"]');
    assert.equal(await dtInputs.count(), 3);
    const startVal = await dtInputs.nth(0).inputValue();
    const plannedEndVal = await dtInputs.nth(1).inputValue();
    const dueVal = await dtInputs.nth(2).inputValue();
    assert.ok(startVal.length > 0, 'Start datetime-local must be non-empty');
    assert.ok(plannedEndVal.length > 0, 'Planned end datetime-local must be non-empty');
    assert.ok(dueVal.length > 0, 'Due datetime-local must be non-empty');
    assert.ok(!startVal.includes('Z') && !startVal.includes('+'), 'Start must not contain Z or +');
    assert.ok(!plannedEndVal.includes('Z') && !plannedEndVal.includes('+'), 'Planned end must not contain Z or +');
    assert.ok(!dueVal.includes('Z') && !dueVal.includes('+'), 'Due must not contain Z or +');

    // Set duration 0/0, submit, expect alert
    await hourInput.fill('0');
    await minuteInput.fill('0');
    const reqBefore = captured.length;
    await page.locator('aside[role="dialog"] button[type="submit"]').click();
    const alert1 = page.getByRole('alert').filter({ hasText: '请至少设置 1 分钟' });
    await alert1.waitFor({ state: 'visible', timeout: 3_000 });
    assert.equal(captured.length, reqBefore,
      'No non-GET request when duration is 0');

    // Set duration 1/30, start > planned end, submit, expect alert
    await hourInput.fill('1');
    await minuteInput.fill('30');
    await dtInputs.nth(0).fill('2026-07-14T15:00');
    await dtInputs.nth(1).fill('2026-07-14T14:00');
    const reqBefore2 = captured.length;
    await page.locator('aside[role="dialog"] button[type="submit"]').click();
    const alert2 = page.getByRole('alert').filter({ hasText: '计划结束时间必须晚于开始时间' });
    await alert2.waitFor({ state: 'visible', timeout: 3_000 });
    assert.equal(captured.length, reqBefore2,
      'No non-GET request when planned end <= start');

    // Set valid local values
    await dtInputs.nth(0).fill('2026-07-14T14:00');
    await dtInputs.nth(1).fill('2026-07-14T15:30');
    await dtInputs.nth(2).fill('2026-07-15T12:00');
    await hourInput.fill('1');
    await minuteInput.fill('30');
    await page.locator('aside[role="dialog"] button[type="submit"]').click();

    // Wait for drawer to close
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 5_000 }).catch(() => undefined);

    // Assert exactly one PUT to /calendar/tasks/task-audit-1 and zero /move
    const putCalls = captured.filter(c => c.method === 'PUT' && c.url === '/api/v1/calendar/tasks/task-audit-1');
    assert.equal(putCalls.length, 1, 'Exactly one PUT to task-audit-1');
    const moveCalls = captured.filter(c => c.url.includes('/move'));
    assert.equal(moveCalls.length, 0, 'Zero /move calls');

    // Unconditionally inspect PUT body
    const putBody = putCalls[0].body as Record<string, unknown>;
    assert.equal(putBody.estimatedDuration, 'PT1H30M');
    assert.equal(putBody.dtStart, '2026-07-14T06:00:00.000Z');
    assert.equal(putBody.plannedEnd, '2026-07-14T07:30:00.000Z');
    assert.equal(putBody.due, '2026-07-15T04:00:00.000Z');
    assert.equal(putBody.calendarId, 'cal-manual-1');

    // ── Part A2: Existing task with no estimated duration ──────────

    await openEventByText(page, '无预估时长任务');

    // Locate duration spinbuttons by accessible names
    const emptyDurHour = page.locator('aside[role="dialog"]')
      .getByRole('spinbutton', { name: '时', exact: true });
    const emptyDurMinute = page.locator('aside[role="dialog"]')
      .getByRole('spinbutton', { name: '分钟', exact: true });

    // Existing task with no duration must show blank inputs, not 0 and 30
    assert.equal(await emptyDurHour.inputValue(), '');
    assert.equal(await emptyDurMinute.inputValue(), '');

    // Close drawer without submitting
    await page.locator('aside[role="dialog"] footer button', { hasText: '取消' }).click();
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 5_000 }).catch(() => undefined);

    // ── Part B: New-task creation ─────────────────────────────────

    // Use Inbox panel + 新建 menu, click 任务
    const inboxPanel = page.getByRole('heading', { name: '收集箱', exact: true }).locator('../..');
    await inboxPanel.getByRole('button', { name: '+ 新建', exact: true }).click();
    await inboxPanel.getByRole('button', { name: '任务', exact: true }).click();
    await page.locator('aside[role="dialog"] h2', { hasText: '新建任务' }).waitFor({ state: 'visible', timeout: 5_000 });

    // Assert duration defaults to 0 and 30
    const newHourInput = page.locator('aside[role="dialog"]')
      .getByRole('spinbutton', { name: '时', exact: true });
    const newMinuteInput = page.locator('aside[role="dialog"]')
      .getByRole('spinbutton', { name: '分钟', exact: true });
    assert.equal(await newHourInput.inputValue(), '0');
    assert.equal(await newMinuteInput.inputValue(), '30');

    // Fill title
    const titleInput = page.locator('aside[role="dialog"] input[type="text"]').first();
    await titleInput.fill('新建任务可靠性测试');

    // Select calendar cal-manual-1 explicitly
    const calSelect = page.locator('aside[role="dialog"] select').first();
    await calSelect.selectOption({ value: 'cal-manual-1' });

    // Fill duration
    await newHourInput.fill('2');
    await newMinuteInput.fill('15');

    // Fill datetime-local fields
    const newDtInputs = page.locator('aside[role="dialog"] input[type="datetime-local"]');
    await newDtInputs.nth(0).fill('2026-07-16T09:00');
    await newDtInputs.nth(1).fill('2026-07-16T11:15');
    await newDtInputs.nth(2).fill('2026-07-17T18:00');

    // Submit and wait for drawer to close
    await page.locator('aside[role="dialog"] button[type="submit"]').click();
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 5_000 }).catch(() => undefined);

    // Assert exactly one POST to /api/v1/calendar/tasks
    const postCalls = captured.filter(c => c.method === 'POST' && c.url === '/api/v1/calendar/tasks');
    assert.equal(postCalls.length, 1, 'Exactly one POST to /api/v1/calendar/tasks');

    // Unconditionally inspect POST body
    const postBody = postCalls[0].body as Record<string, unknown>;
    assert.equal(postBody.title, '新建任务可靠性测试');
    assert.equal(postBody.estimatedDuration, 'PT2H15M');
    assert.equal(postBody.dtStart, '2026-07-16T01:00:00.000Z');
    assert.equal(postBody.plannedEnd, '2026-07-16T03:15:00.000Z');
    assert.equal(postBody.due, '2026-07-17T10:00:00.000Z');
    assert.equal(postBody.calendarId, 'cal-manual-1');

    await page.close();
  } finally {
    await context.close();
  }
}

// ─── Scenario K: Timeline density, month capacity, local timezone ─────

async function runScenarioK(browser: Browser, baseUrl: string) {
  // ── K1 Timeline density and visual ──────────────────────────────
  {
    const context = await newFixedContext(browser, {
      viewport: { width: 1440, height: 1000 },
      timezoneId: DEFAULT_TIMEZONE_ID,
    });
    try {
      await context.addInitScript(() => {
        localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
      });
      await context.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const fullPath = url.pathname + url.search;
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify(mockApiResponse(fullPath, undefined, true)),
        });
      });

      const page = await context.newPage();
      await setScenarioKClock(page);
      const consoleErrors: string[] = [];
      page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });

      await page.goto(`${baseUrl}/calendar?view=timeline`, { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined);
      await page.waitForSelector('.fc-event, .calendar-event-card', { timeout: 8_000 }).catch(() => undefined);

      const longCard = page.locator('.fc-timegrid-event:has-text("密度详情事件") .calendar-event-card').first();
      await longCard.waitFor({ state: 'visible', timeout: 5_000 });

      const compactCard = page.locator('.fc-timegrid-event:has-text("密度紧凑事件") .calendar-event-card').first();
      await compactCard.waitFor({ state: 'visible', timeout: 5_000 });

      // Long card assertions
      const longLevel = await longCard.getAttribute('data-content-level');
      assert.equal(longLevel, '5', 'Long card must have data-content-level="5"');

      const longBorderLeftWidth = await longCard.evaluate(el => getComputedStyle(el).borderLeftWidth);
      assert.equal(longBorderLeftWidth, '3px', 'Long card must have 3px left border');

      const longBorderLeftColor = await longCard.evaluate(el => getComputedStyle(el).borderLeftColor);
      assert.equal(longBorderLeftColor, 'rgb(170, 68, 0)', 'Long card border must be calendar accent color');

      const longBg = await longCard.evaluate(el => getComputedStyle(el).backgroundColor);
      const longAlpha = extractAlpha(longBg);
      assert.ok(Math.abs(longAlpha - 0.15) < 0.03,
        `Long card background alpha ${longAlpha} must be approximately 0.15`);

      // Visible sub-elements in long card
      assert.ok(await longCard.locator('.calendar-event-location').isVisible({ timeout: 1_000 }).catch(() => false),
        'Long card must show location');
      assert.ok(await longCard.locator('.calendar-event-source').isVisible({ timeout: 1_000 }).catch(() => false),
        'Long card must show source label');
      assert.ok(await longCard.locator('.calendar-event-description').isVisible({ timeout: 1_000 }).catch(() => false),
        'Long card must show description summary');
      assert.ok(await longCard.locator('.calendar-event-rrule').isVisible({ timeout: 1_000 }).catch(() => false),
        'Long card must show recurrence icon');

      // Compact card assertions
      const compactLevel = await compactCard.getAttribute('data-content-level');
      assert.equal(compactLevel, '1', 'Compact card must have data-content-level="1"');

      assert.ok(!await compactCard.locator('.calendar-event-location').isVisible({ timeout: 500 }).catch(() => false),
        'Compact card must hide location');
      assert.ok(!await compactCard.locator('.calendar-event-source').isVisible({ timeout: 500 }).catch(() => false),
        'Compact card must hide source label');
      assert.ok(!await compactCard.locator('.calendar-event-description').isVisible({ timeout: 500 }).catch(() => false),
        'Compact card must hide description');
      assert.ok(!await compactCard.locator('.calendar-event-rrule').isVisible({ timeout: 500 }).catch(() => false),
        'Compact card must hide recurrence icon');

      assert.deepEqual(consoleErrors, [], 'K1 must not log console errors');
      await page.close();
    } finally {
      await context.close();
    }
  }

  // ── K2/K3/K4 Month capacity ────────────────────────────────────
  {
    const context = await newFixedContext(browser, {
      viewport: { width: 1440, height: 1200 },
      timezoneId: DEFAULT_TIMEZONE_ID,
    });
    try {
      await context.addInitScript(() => {
        localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
      });
      await context.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const fullPath = url.pathname + url.search;
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify(mockApiResponse(fullPath, undefined, true)),
        });
      });

      const page = await context.newPage();
      await setScenarioKClock(page);
      const consoleErrors: string[] = [];
      page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });

      await page.goto(`${baseUrl}/calendar?view=month`, { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined);
      await page.waitForSelector('.fc-event, .calendar-event-card', { timeout: 8_000 }).catch(() => undefined);

      // K2: Tall board — all capacity titles visible, no more link
      await page.evaluate(() => {
        const board = document.querySelector('.calendar-board') as HTMLElement | null;
        if (board) {
          board.style.flex = '0 0 auto';
          board.style.height = '900px';
        }
      });
      await page.evaluate(() => window.dispatchEvent(new Event('resize')));

      const capacityDay = page.locator('.fc-daygrid-day[data-date="2026-07-15"]');
      const capacityMoreLink = capacityDay.locator('.fc-more-link');
      await capacityMoreLink.waitFor({ state: 'hidden', timeout: 5_000 });
      const moreLinkVisible = await capacityMoreLink.isVisible().catch(() => false);
      assert.ok(!moreLinkVisible, 'K2: No +N more link with tall board');

      for (let i = 1; i <= 5; i++) {
        const title = capacityDay.locator('.fc-event').filter({ hasText: `容量日程 ${i}` }).first();
        await title.waitFor({ state: 'visible', timeout: 5_000 });
      }

      // K3: Short board — more link appears
      await page.evaluate(() => {
        const board = document.querySelector('.calendar-board') as HTMLElement | null;
        if (board) board.style.height = '280px';
      });
      await page.evaluate(() => window.dispatchEvent(new Event('resize')));

      const shortMoreLink = capacityMoreLink.first();
      await shortMoreLink.waitFor({ state: 'visible', timeout: 5_000 });

      // K4: Click more link, popover shows all capacity titles
      const moreLinkCount = await capacityMoreLink.count();
      assert.ok(moreLinkCount > 0, 'K4: At least one more link must exist');

      await shortMoreLink.click();

      const popover = page.locator('.fc-more-popover');
      await popover.waitFor({ state: 'visible', timeout: 3_000 });

      for (let i = 1; i <= 5; i++) {
        const inPopover = await popover.getByText(`容量日程 ${i}`, { exact: true }).isVisible({ timeout: 1_000 }).catch(() => false);
        assert.ok(inPopover, `K4: 容量日程 ${i} must be in more popover`);
      }

      const closeBtn = popover.locator('.fc-popover-close');
      await closeBtn.click();
      await popover.waitFor({ state: 'hidden', timeout: 3_000 });
      const popoverClosed = await page.locator('.fc-more-popover').isVisible().catch(() => false);
      assert.ok(!popoverClosed, 'K4: Popover must close without opening editor');

      assert.deepEqual(consoleErrors, [], 'K2-K4 must not log console errors');
      await page.close();
    } finally {
      await context.close();
    }
  }

  // ── K5 Browser-local timezone ───────────────────────────────────
  {
    const context = await newFixedContext(browser, {
      viewport: { width: 1440, height: 1000 },
      timezoneId: 'America/New_York',
    });
    try {
      await context.addInitScript(() => {
        localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
      });
      await context.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const fullPath = url.pathname + url.search;
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify(mockApiResponse(fullPath, undefined, true)),
        });
      });

      const page = await context.newPage();
      await setScenarioKClock(page);
      const consoleErrors: string[] = [];
      page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });

      await page.goto(`${baseUrl}/calendar?view=month`, { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined);
      await page.waitForSelector('.fc-event, .calendar-event-card', { timeout: 8_000 }).catch(() => undefined);

      const tzEvent = page.locator('.fc-event').filter({ hasText: '本地时区验证事件' }).first();
      await tzEvent.waitFor({ state: 'visible', timeout: 5_000 });

      const timeText = await tzEvent.locator('.calendar-event-time, .fc-event-time').first().textContent({ timeout: 3_000 });
      assert.equal(timeText?.trim(), '10:00',
        `K5: Event time must be 10:00 in America/New_York, got "${timeText}"`);

      assert.deepEqual(consoleErrors, [], 'K5 must not log console errors');
      await page.close();
    } finally {
      await context.close();
    }
  }
}

async function setScenarioKClock(page: Page) {
  await page.clock.setFixedTime(new Date('2026-07-20T12:00:00.000Z'));
}

/**
 * 统计两张截图之间 RGB 不同的像素数（最小 PNG 解码：8 位 RGB/RGBA、无隔行）。
 * 用于截图稳定性断言的容差比较：居中弹窗（圆角 + 独立滚动层）在软渲染下
 * 偶尔出现个位数的抗锯齿像素抖动，属于渲染噪声而非 UI 不稳定；
 * 真实的视觉回归会产生成百上千的像素差异，阈值可将其拦下。
 */
function pngDiffPixelCount(pngA: Buffer, pngB: Buffer): number {
  const decode = (buf: Buffer) => {
    let off = 8;
    let w = 0;
    let h = 0;
    let colorType = 0;
    const idat: Buffer[] = [];
    while (off + 8 <= buf.length) {
      const len = buf.readUInt32BE(off);
      const type = buf.toString('ascii', off + 4, off + 8);
      const data = buf.subarray(off + 8, off + 8 + len);
      if (type === 'IHDR') {
        w = data.readUInt32BE(0);
        h = data.readUInt32BE(4);
        colorType = data[9];
      } else if (type === 'IDAT') {
        idat.push(data);
      }
      off += 12 + len;
      if (type === 'IEND') break;
    }
    return { w, h, colorType, raw: inflateSync(Buffer.concat(idat)) };
  };

  const unfilter = (img: { w: number; h: number; colorType: number; raw: Buffer }) => {
    const bpp = img.colorType === 2 ? 3 : 4;
    const stride = img.w * bpp;
    const out = Buffer.alloc(img.h * stride);
    let pos = 0;
    for (let y = 0; y < img.h; y++) {
      const filter = img.raw[pos++];
      const line = img.raw.subarray(pos, pos + stride);
      pos += stride;
      for (let x = 0; x < stride; x++) {
        const a = x >= bpp ? out[y * stride + x - bpp] : 0;
        const b = y > 0 ? out[(y - 1) * stride + x] : 0;
        const c = x >= bpp && y > 0 ? out[(y - 1) * stride + x - bpp] : 0;
        let v = line[x];
        if (filter === 1) v = (v + a) & 255;
        else if (filter === 2) v = (v + b) & 255;
        else if (filter === 3) v = (v + ((a + b) >> 1)) & 255;
        else if (filter === 4) {
          const p = a + b - c;
          const pa = Math.abs(p - a);
          const pb = Math.abs(p - b);
          const pc = Math.abs(p - c);
          v = (v + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c)) & 255;
        }
        out[y * stride + x] = v;
      }
    }
    return out;
  };

  const imgA = decode(pngA);
  const imgB = decode(pngB);
  if (imgA.w !== imgB.w || imgA.h !== imgB.h) return imgA.w * imgA.h + imgB.w * imgB.h;
  const pxA = unfilter(imgA);
  const pxB = unfilter(imgB);
  const bpp = imgA.colorType === 2 ? 3 : 4;
  let count = 0;
  for (let i = 0; i < imgA.w * imgA.h; i++) {
    const o = i * bpp;
    if (pxA[o] !== pxB[o] || pxA[o + 1] !== pxB[o + 1] || pxA[o + 2] !== pxB[o + 2]) count++;
  }
  return count;
}

// ─── Scenario L: Task 8 editor UX at 1440x1000 and 390x844 ───────────

async function runScenarioL(browser: Browser, baseUrl: string) {
  for (const [width, height] of [[1440, 1000], [390, 844]] as const) {
    const context = await browser.newContext({
      viewport: { width, height },
      timezoneId: DEFAULT_TIMEZONE_ID,
    });
    try {
      const captured: CapturedRequest[] = [];
      let conflictSeq = 0;
      const consoleErrors: string[] = [];
      await context.addInitScript(() => {
        localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
      });
      await context.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const fullPath = url.pathname + url.search;
        const method = route.request().method();
        if (method !== 'GET') {
          const postBody = route.request().postDataJSON();
          captured.push({ url: fullPath, method, body: postBody });
        }
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify(mockApiResponse(fullPath, method)),
        });
      });
      await context.route('**/api/v1/calendar/outlook/events/writeback', async route => {
        const body = route.request().postDataJSON();
        captured.push({ url: 'writeback', method: 'POST', body });
        conflictSeq++;
        if (conflictSeq === 1) {
          return route.fulfill({
            status: 409, contentType: 'application/json',
            body: JSON.stringify(conflictResponse('Outlook Updated Title', 'etag-new-001')),
          });
        }
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify({ code: 0, message: 'OK',
            data: { status: 'updated' }, timestamp: new Date().toISOString() }),
        });
      });

      const page = await context.newPage();
      let last409Url: string | null = null;
      page.on('response', response => {
        if (response.status() === 409) last409Url = response.url();
      });
      page.on('console', message => {
        if (message.type() !== 'error') return;
        // L7 deliberately triggers a 409 conflict on the writeback endpoint, so
        // the browser logs the failed resource load; that network message is
        // expected. The ignore is narrowly URL-scoped: any other 409 must fail.
        if (
          message.text().includes('409 (Conflict)')
          && last409Url?.includes('/calendar/outlook/events/writeback')
        ) {
          return;
        }
        consoleErrors.push(message.text());
      });
      await openCalendarMonth(page, baseUrl);
      await openEventByText(page, 'Outlook 可编辑事件', true);
      const aside = page.locator('aside[role="dialog"]');

      // L1: manual and Outlook editors share every section
      for (const section of ['基本信息', '高级', '协作', '会议', '附件', '重复']) {
        await aside.getByRole('button', { name: section, exact: true }).waitFor({ state: 'visible', timeout: 3_000 });
      }

      // L2: Tiptap toolbar and content visible and non-overlapping
      const toolbar = aside.locator('[role="toolbar"][aria-label="描述格式工具栏"]').first();
      await toolbar.waitFor({ state: 'visible', timeout: 3_000 });
      const tiptap = aside.locator('[data-description-html-preview]').first();
      await tiptap.waitFor({ state: 'visible', timeout: 5_000 });
      const toolbarBox = await toolbar.boundingBox();
      const tiptapBox = await tiptap.boundingBox();
      assert.ok(toolbarBox && tiptapBox, 'Toolbar and Tiptap content must both be visible');
      assert.ok(tiptapBox!.y >= toolbarBox!.y + toolbarBox!.height - 1,
        'Tiptap content must not overlap its toolbar');

      // L3: Outlook additional info collapsed by default, hidden count visible, no secret leak
      const outlookSection = aside.locator('section[data-event-editor-section]', { hasText: 'Outlook 附加信息' });
      await outlookSection.waitFor({ state: 'visible', timeout: 3_000 });
      assert.ok((await outlookSection.textContent())?.includes('3 项敏感字段已隐藏'),
        'Hidden-field count must be visible while the section is collapsed');
      const collapsedText = await aside.textContent();
      assert.ok(!collapsedText?.includes('PRIVATE-BODY-SECRET-MARKER'),
        'Raw body/secret must not leak while additional info is collapsed');
      await aside.getByRole('button', { name: /Outlook 附加信息/ }).click();
      const expandedText = await aside.textContent();
      assert.ok(expandedText?.includes('会议详情'), 'Expanded additional info must show the allowlisted group');
      assert.ok(expandedText?.includes('PRIVATE-BODY-SECRET-MARKER'),
        'Expanded allowlisted items may show their label/value summary');

      // L4: long attendee/category values wrap within the drawer
      await aside.getByRole('button', { name: '高级', exact: true }).click();
      const longCategory = '输入的超长分类名称用于验证窄屏下的换行行为与抽屉边界展示效果';
      const categoryInput = aside.locator('input[placeholder="输入分类后回车添加"]');
      await categoryInput.fill(longCategory);
      await categoryInput.press('Enter');
      await aside.locator('.event-category-chip').filter({ hasText: longCategory })
        .waitFor({ state: 'visible', timeout: 3_000 });

      await aside.getByRole('button', { name: '协作', exact: true }).click();
      await aside.getByRole('button', { name: '添加参会者', exact: true }).click();
      const longName = '一个非常长的参会者姓名用于验证换行与抽屉边界不溢出';
      const longEmail = 'this-is-a-very-long-email-address-that-must-wrap-inside-the-drawer@example.com';
      await aside.getByRole('textbox', { name: '参会者 1 姓名' }).fill(longName);
      await aside.getByRole('textbox', { name: '参会者 1 邮箱' }).fill(longEmail);
      await assertDrawerNoHorizontalOverflow(page);

      // L5: meeting and attachment controls fit
      await aside.getByRole('button', { name: '会议', exact: true }).click();
      await aside.getByText('在线会议', { exact: true }).waitFor({ state: 'visible', timeout: 3_000 });
      await aside.getByText('会议提供方', { exact: true }).waitFor({ state: 'visible', timeout: 3_000 });
      await aside.getByRole('button', { name: '附件', exact: true }).click();
      await aside.locator('[data-attachment-row]').first().waitFor({ state: 'visible', timeout: 3_000 });
      assert.ok((await aside.textContent())?.includes('Outlook 附件（只读）'),
        'Outlook attachments must be visible but read-only');
      await assertDrawerNoHorizontalOverflow(page);

      // L6: typed field-level preview diff is readable
      await aside.locator('input[type="text"]').first().fill('编辑后标题');
      await aside.locator('button[type="submit"]').click();
      await waitForWritebackPreview(page);
      const baSection = page.locator('section[aria-label="变更前后对比"]');
      await baSection.waitFor({ state: 'visible', timeout: 3_000 });
      assert.equal(await page.locator('div[role="dialog"][aria-modal="true"] pre').count(), 0,
        'Preview must not render raw JSON');
      const diffTitleRow = baSection.locator('li[aria-label^="标题：修改"]');
      await diffTitleRow.waitFor({ state: 'visible', timeout: 3_000 });
      assert.ok((await diffTitleRow.getAttribute('aria-label'))?.includes('→'),
        'Field-level diff rows must be readable');

      // L7: 412 conflict recompare keeps the draft; canceling returns to the editor
      await writebackConfirmBtn(page).click();
      await waitForConflict(page);
      await page.locator('button', { hasText: '重新比较' }).click();
      await waitForWritebackPreview(page);
      await page.locator('div[role="dialog"][aria-modal="true"] button', { hasText: '取消' }).click();
      const editorStillOpen = await aside.isVisible({ timeout: 3_000 }).catch(() => false);
      assert.ok(editorStillOpen, 'Editor must remain open after canceling the recompare preview');
      const titleAfterRecompare = await aside.locator('input[type="text"]').first().inputValue();
      assert.ok(titleAfterRecompare.includes('编辑后标题'),
        'Draft must survive the 412 recompare flow');

      // L8: screenshots are nonblank and stable
      const shot1 = await page.screenshot();
      const shot2 = await page.screenshot();
      assert.ok(shot1.length > 15_000,
        `Screenshot must be nonblank at ${width}x${height} (${shot1.length} bytes)`);
      const byteEqual = shot1.length === shot2.length && Buffer.compare(shot1, shot2) === 0;
      const diffPixels = byteEqual ? 0 : pngDiffPixelCount(shot1, shot2);
      assert.ok(diffPixels <= 50,
        `Screenshots must be stable across captures (${diffPixels} differing pixels)`);

      assert.deepEqual(consoleErrors, [], `Scenario L at ${width}x${height} must not log console errors`);
      await page.close();
    } finally {
      await context.close();
    }
  }
}

async function assertDrawerNoHorizontalOverflow(page: Page) {
  const overflow = await page.evaluate(() => {
    const drawer = document.querySelector('aside[role="dialog"]');
    if (!drawer) return -1;
    return drawer.scrollWidth - drawer.clientWidth;
  });
  assert.ok(overflow >= 0 && overflow <= 2,
    `Drawer must not overflow horizontally (got ${overflow}px)`);
}

// ─── Scenario M: Task 8 calendar visual semantics ────────────────────

async function runScenarioM(browser: Browser, baseUrl: string) {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 1000 },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  try {
    const consoleErrors: string[] = [];
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
    });
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, undefined, true)),
      });
    });

    const page = await context.newPage();
    await setScenarioKClock(page);
    page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });

    await page.goto(`${baseUrl}/calendar?view=timeline`, { waitUntil: 'domcontentloaded' });
    await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined);
    await page.waitForSelector('.fc-event, .calendar-event-card', { timeout: 8_000 }).catch(() => undefined);

    // M1: importance=high adds emphasis without replacing the calendar color
    const importantCard = page
      .locator('.fc-timegrid-event:has-text("重要空闲事件") .calendar-event-card')
      .first();
    await importantCard.waitFor({ state: 'visible', timeout: 5_000 });
    assert.equal(await importantCard.getAttribute('data-content-level'), '5',
      'Long semantic card must reach the highest content level');
    assert.ok((await importantCard.getAttribute('class'))?.includes('calendar-event--important'),
      'importance=high must add an important modifier class');
    const importantBg = await importantCard.evaluate(el => getComputedStyle(el).backgroundColor);
    const importantAlpha = extractAlpha(importantBg);
    assert.ok(Math.abs(importantAlpha - 0.15) < 0.03,
      'importance=high must keep the calendar-tinted translucent background');
    const importantBorder = await importantCard.evaluate(el => getComputedStyle(el).borderLeftColor);
    assert.equal(importantBorder, 'rgb(0, 68, 204)',
      'importance=high must keep the calendar accent border color');
    const importanceBadge = importantCard.locator('.calendar-event-importance');
    await importanceBadge.waitFor({ state: 'visible', timeout: 3_000 });
    assert.ok((await importanceBadge.textContent())?.includes('重要'), 'Important badge must read 重要');
    const reminderBadge = importantCard.locator('.calendar-event-reminder');
    await reminderBadge.waitFor({ state: 'visible', timeout: 3_000 });
    assert.equal(await reminderBadge.getAttribute('aria-label'), '提醒',
      'Reminder icon must carry the 提醒 accessible label');

    // M2: showAs=free renders a non-blocking 空闲 status
    const freeBadge = importantCard.locator('.calendar-event-showas--free');
    await freeBadge.waitFor({ state: 'visible', timeout: 3_000 });
    assert.equal((await freeBadge.textContent())?.trim(), '空闲', 'free event must show 空闲');
    const freeBorderStyle = await freeBadge.evaluate(el => getComputedStyle(el).borderTopStyle);
    assert.equal(freeBorderStyle, 'dashed', 'free status must use a non-blocking dashed visual');

    // M3: showAs=tentative renders 暂定
    const tentativeCard = page
      .locator('.fc-timegrid-event:has-text("暂定事件") .calendar-event-card')
      .first();
    await tentativeCard.waitFor({ state: 'visible', timeout: 5_000 });
    const tentativeBadge = tentativeCard.locator('.calendar-event-showas--tentative');
    await tentativeBadge.waitFor({ state: 'visible', timeout: 3_000 });
    assert.equal((await tentativeBadge.textContent())?.trim(), '暂定', 'tentative event must show 暂定');

    // M4: reminder/importance icons appear only at the highest content level
    const compactCard = page.locator('.fc-timegrid-event:has-text("密度紧凑事件") .calendar-event-card').first();
    await compactCard.waitFor({ state: 'visible', timeout: 5_000 });
    assert.equal(await compactCard.getAttribute('data-content-level'), '1',
      'Compact card must stay at level 1');
    assert.ok(!await compactCard.locator('.calendar-event-importance').isVisible().catch(() => false),
      'Importance icon must not appear at lower content levels');
    assert.ok(!await compactCard.locator('.calendar-event-reminder').isVisible().catch(() => false),
      'Reminder icon must not appear at lower content levels');

    assert.deepEqual(consoleErrors, [], 'Scenario M must not log console errors');
    await page.close();
  } finally {
    await context.close();
  }
}

// ─── Scenario N: Task 8 card title priority and summary styles ────────

const LONG_TITLE_EVENT = '超长标题事件';

async function runScenarioN(browser: Browser, baseUrl: string) {
  // ── N1/N2: Month daygrid title priority and secondary visibility ──
  for (const [width, height] of [[390, 844], [1440, 1000]] as const) {
    const context = await browser.newContext({
      viewport: { width, height },
      timezoneId: DEFAULT_TIMEZONE_ID,
    });
    try {
      const consoleErrors: string[] = [];
      await context.addInitScript(() => {
        localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
      });
      await context.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const fullPath = url.pathname + url.search;
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify(mockApiResponse(fullPath)),
        });
      });

      const page = await context.newPage();
      page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });
      await setScenarioKClock(page);
      await page.goto(`${baseUrl}/calendar?view=month`, { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined);
      await page.waitForSelector('.fc-event, .calendar-event-card', { timeout: 8_000 }).catch(() => undefined);

      const dayCell = page.locator('.fc-daygrid-day[data-date="2026-07-16"]');
      const card = dayCell.locator('.calendar-event-card', { hasText: LONG_TITLE_EVENT }).first();
      await card.waitFor({ state: 'visible', timeout: 5_000 });

      const title = card.locator('.calendar-event-title');
      const titleMetrics = await title.evaluate(el => {
        const s = getComputedStyle(el);
        return {
          width: el.clientWidth,
          scrollWidth: el.scrollWidth,
          overflow: s.overflow,
          textOverflow: s.textOverflow,
          whiteSpace: s.whiteSpace,
        };
      });
      assert.ok(titleMetrics.width > 0,
        `N1: daygrid title must keep nonzero width at ${width}px (got ${titleMetrics.width})`);
      assert.equal(titleMetrics.overflow, 'hidden',
        `N1: daygrid title must clip at ${width}px`);
      assert.equal(titleMetrics.textOverflow, 'ellipsis',
        `N1: daygrid title must ellipsize at ${width}px`);
      assert.equal(titleMetrics.whiteSpace, 'nowrap',
        `N1: daygrid title must stay on one line at ${width}px`);
      assert.ok(titleMetrics.scrollWidth >= titleMetrics.width,
        `N1: long daygrid title must remain ellipsizable at ${width}px`);

      const timeVisible = await card.locator('.calendar-event-time').isVisible({ timeout: 500 }).catch(() => false);
      const dotVisible = await card.locator('.calendar-event-dot').isVisible({ timeout: 500 }).catch(() => false);
      if (width === 390) {
        assert.ok(!timeVisible, 'N2: time must be hidden in narrow daygrid cards');
        assert.ok(!dotVisible, 'N2: dot must be hidden in the narrowest daygrid cards');
      } else {
        assert.ok(timeVisible, 'N2: time must stay visible in wide daygrid cards');
        assert.ok(dotVisible, 'N2: dot must stay visible in wide daygrid cards');
      }

      // N2b: as a daygrid card narrows, the time must hide before the dot,
      // with a band in between where the dot alone survives.
      const cascade = await card.evaluate(el => {
        const time = el.querySelector('.calendar-event-time');
        const dot = el.querySelector('.calendar-event-dot');
        const samples = [];
        for (let width = 140; width >= 24; width -= 4) {
          el.style.width = `${width}px`;
          el.style.flex = '0 0 auto';
          samples.push({
            width,
            time: !!time && getComputedStyle(time).display !== 'none',
            dot: !!dot && getComputedStyle(dot).display !== 'none',
          });
        }
        el.style.width = '';
        el.style.flex = '';
        return samples;
      });
      const firstTimeHidden = cascade.find(s => !s.time);
      const firstDotHidden = cascade.find(s => !s.dot);
      assert.ok(firstTimeHidden && firstDotHidden,
        'N2b: time and dot must each hide at some narrow card width');
      assert.ok(firstTimeHidden!.width > firstDotHidden!.width,
        `N2b: time must hide before the dot as the card narrows (time at ${firstTimeHidden!.width}px, dot at ${firstDotHidden!.width}px)`);
      assert.ok(cascade.some(s => !s.time && s.dot),
        'N2b: a narrow width must hide the time while keeping the dot visible');

      // N3: summary computed style through the event editor
      await page.locator('.fc-more-popover .fc-popover-close').click().catch(() => undefined);
      await openEventByText(page, 'Outlook 可编辑事件', true);
      const summary = page.locator('aside[role="dialog"] .event-editor-section-summary').first();
      await summary.waitFor({ state: 'visible', timeout: 5_000 });
      const summaryStyle = await summary.evaluate(el => {
        const s = getComputedStyle(el);
        return {
          fontSize: s.fontSize,
          fontWeight: s.fontWeight,
          color: s.color,
          whiteSpace: s.whiteSpace,
          overflow: s.overflow,
          textOverflow: s.textOverflow,
        };
      });
      assert.equal(summaryStyle.fontSize, '12px',
        `N3: summary font-size must be 0.75rem at ${width}px (got ${summaryStyle.fontSize})`);
      assert.equal(summaryStyle.fontWeight, '400',
        `N3: summary must be normal-weight at ${width}px (got ${summaryStyle.fontWeight})`);
      // 2026-09-19 视觉基调 v1：muted 色随新色板更新（--pim-text-muted #64748b → #717581）
      assert.equal(summaryStyle.color, 'rgb(113, 117, 129)',
        `N3: summary must be muted at ${width}px (got ${summaryStyle.color})`);
      assert.equal(summaryStyle.whiteSpace, 'nowrap',
        `N3: summary must be single-line at ${width}px`);
      assert.equal(summaryStyle.overflow, 'hidden',
        `N3: summary must clip at ${width}px`);
      assert.equal(summaryStyle.textOverflow, 'ellipsis',
        `N3: summary must ellipsize at ${width}px`);
      const sectionTitleStyle = await page
        .locator('aside[role="dialog"] .event-editor-section-title')
        .first()
        .evaluate(el => {
          const s = getComputedStyle(el);
          return { overflow: s.overflow, textOverflow: s.textOverflow, whiteSpace: s.whiteSpace };
        });
      assert.equal(sectionTitleStyle.overflow, 'hidden',
        `N3: section title must clip at ${width}px`);
      assert.equal(sectionTitleStyle.textOverflow, 'ellipsis',
        `N3: section title must ellipsize at ${width}px`);
      assert.equal(sectionTitleStyle.whiteSpace, 'nowrap',
        `N3: section title must stay on one line at ${width}px`);

      assert.deepEqual(consoleErrors, [], `Scenario N month at ${width}px must not log console errors`);
      await page.close();
    } finally {
      await context.close();
    }
  }

  // ── N4: Timegrid title ellipsis and no horizontal overflow ───────
  {
    const context = await browser.newContext({
      viewport: { width: 390, height: 844 },
      timezoneId: DEFAULT_TIMEZONE_ID,
    });
    try {
      const consoleErrors: string[] = [];
      await context.addInitScript(() => {
        localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
      });
      await context.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const fullPath = url.pathname + url.search;
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify(mockApiResponse(fullPath)),
        });
      });

      const page = await context.newPage();
      page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });
      // Pin the calendar to 2026-07-16 so the long-title fixture is in the day view.
      await page.clock.setFixedTime(new Date('2026-07-16T04:00:00.000Z'));
      await page.goto(`${baseUrl}/calendar?view=timeline`, { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined);
      await page.waitForSelector('.fc-event, .calendar-event-card', { timeout: 8_000 }).catch(() => undefined);

      const card = page
        .locator(`.fc-timegrid-event:has-text("${LONG_TITLE_EVENT}") .calendar-event-card`)
        .first();
      await card.waitFor({ state: 'visible', timeout: 5_000 });

      const metrics = await card.evaluate(el => {
        const title = el.querySelector<HTMLElement>('.calendar-event-title');
        const time = el.querySelector<HTMLElement>('.calendar-event-time');
        if (!title) return null;
        const titleStyle = getComputedStyle(title);
        return {
          cardScrollWidth: el.scrollWidth,
          cardClientWidth: el.clientWidth,
          titleClientWidth: title.clientWidth,
          titleScrollWidth: title.scrollWidth,
          titleOverflow: titleStyle.overflow,
          titleTextOverflow: titleStyle.textOverflow,
          titleWhiteSpace: titleStyle.whiteSpace,
          titleTop: title.offsetTop,
          timeTop: time ? time.offsetTop : -1,
        };
      });
      assert.ok(metrics !== null, 'N4: timegrid title must exist');
      assert.ok((metrics!.cardScrollWidth - metrics!.cardClientWidth) <= 2,
        `N4: timegrid card must not overflow horizontally (got ${metrics!.cardScrollWidth - metrics!.cardClientWidth}px)`);
      assert.ok(metrics!.titleClientWidth > 0,
        `N4: timegrid title must keep nonzero width (got ${metrics!.titleClientWidth})`);
      assert.ok(metrics!.titleClientWidth <= metrics!.cardClientWidth,
        'N4: timegrid title must stay inside its card');
      assert.ok(metrics!.titleScrollWidth >= metrics!.titleClientWidth,
        'N4: long timegrid title must remain ellipsizable');
      assert.equal(metrics!.titleOverflow, 'hidden');
      assert.equal(metrics!.titleTextOverflow, 'ellipsis');
      assert.equal(metrics!.titleWhiteSpace, 'nowrap');
      assert.ok(metrics!.titleTop <= metrics!.timeTop,
        'N4: timegrid title must stay above the time (vertical order preserved)');

      assert.deepEqual(consoleErrors, [], 'Scenario N timeline must not log console errors');
      await page.close();
    } finally {
      await context.close();
    }
  }
}

function extractAlpha(bg: string): number {
  const rgba = bg.match(/^rgba\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*([\d.]+)\s*\)$/);
  if (rgba) {
    const alpha = Number(rgba[1]);
    if (alpha <= 0) throw new Error(`Expected translucent background, got ${bg}`);
    return alpha;
  }
  const colorMatch = bg.match(/^color\(\s*(?:srgb\s+)?[\d.]+\s+[\d.]+\s+[\d.]+\s*\/\s*([\d.]+)\s*\)$/);
  if (colorMatch) {
    const alpha = Number(colorMatch[1]);
    if (alpha <= 0) throw new Error(`Expected translucent background, got ${bg}`);
    return alpha;
  }
  throw new Error(`Unable to extract alpha from computed background: ${bg}`);
}

function assertMobileCalendarHeightFallback() {
  const css = readFileSync('src/client-web/src/index.css', 'utf8');
  assert.match(css,
    /\.calendar-board\s*\{[^}]*height:\s*24rem;[^}]*height:\s*max\(24rem,\s*calc\(100dvh\s*-\s*28rem\)\);/,
    'mobile calendar board must keep a fixed-height fallback before the dynamic viewport height');
}

function assertTaskEditorUsesAtomicUpdate() {
  const source = readFileSync('src/client-web/src/dialogs/TaskEditorDialog.tsx', 'utf8');
  assert.doesNotMatch(source, /\bmoveTask\b/,
    'task editor must schedule and update through one atomic request');
}

async function assertDialogInViewport(page: Page, selector: string) {
  const info = await page.evaluate((sel) => {
    const el = document.querySelector(sel);
    if (!el) return null;
    const rect = el.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const overflowX = Math.max(0, Math.ceil(rect.right) - vw);
    const overflowY = Math.max(0, Math.ceil(rect.bottom) - vh);
    const clipped = Array.from(el.querySelectorAll('button'))
      .filter(b => (b.textContent ?? '').trim().length > 0)
      .filter(b => {
        const s = getComputedStyle(b);
        return s.whiteSpace === 'nowrap' && b.scrollWidth > Math.ceil(b.clientWidth) + 2;
      })
      .map(b => (b.textContent ?? '').trim());
    // Check for overlapping text between dialog and underlying editor
    const dialogRects: DOMRect[] = [];
    el.querySelectorAll('*').forEach(child => {
      const r = child.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) dialogRects.push(r);
    });
    let overlapCount = 0;
    const editorEl = document.querySelector('aside[role="dialog"]');
    if (editorEl && editorEl !== el) {
      editorEl.querySelectorAll('*').forEach(child => {
        const r = child.getBoundingClientRect();
        if (r.width <= 0 || r.height <= 0) return;
        for (const dr of dialogRects) {
          if (r.left < dr.right && r.right > dr.left && r.top < dr.bottom && r.bottom > dr.top) {
            overlapCount++;
            break;
          }
        }
      });
    }
    return { visible: true, overflowX, overflowY, clipped, overlapCount, insideViewport: rect.right <= vw + 2 && rect.left >= -2 && rect.bottom <= vh + 2 && rect.top >= -2 };
  }, selector);

  assert.ok(info, `${selector} must be found`);
  assert.ok(info!.insideViewport, 'Dialog must stay inside viewport horizontally');
  assert.equal(info!.overflowX, 0, 'No horizontal overflow');
  assert.equal(info!.overflowY, 0, 'No vertical overflow');
  assert.deepEqual(info!.clipped, [], 'No clipped command text');
  // Overlap check only applies when the tested dialog is NOT a modal,
  // because modals naturally overlap the underlying editor.
  if (selector !== 'div[role="dialog"][aria-modal="true"]') {
    assert.equal(info!.overlapCount, 0, 'No incoherent element overlap');
  }
}

// ─── Route assertion ─────────────────────────────────────────────────

async function assertRoute(page: Page, route: string, width: number, height: number) {
  const result = await page.evaluate((forbidden) => {
    const mainText = (document.querySelector('main')?.textContent ?? '').trim();
    const bodyText = document.body.textContent ?? '';
    const clippedButtons = Array.from(document.querySelectorAll('button'))
      .filter(button => (button.textContent ?? '').trim().length > 0)
      .filter(button => {
        const style = getComputedStyle(button);
        return style.whiteSpace === 'nowrap'
          && button.scrollWidth > Math.ceil(button.clientWidth) + 2;
      })
      .map(button => (button.textContent ?? '').trim());
    const negativeBoxes = Array.from(document.querySelectorAll('body *'))
      .filter(element => {
        const rect = element.getBoundingClientRect();
        return rect.width < 0 || rect.height < 0;
      }).length;
    const englishHeadings = Array.from(document.querySelectorAll('h1,h2,[role="heading"]'))
      .map(element => (element.textContent ?? '').trim())
      .filter(text => forbidden.includes(text));
    return {
      bodyText,
      mainTextLength: mainText.length,
      clippedButtons,
      negativeBoxes,
      englishHeadings,
      horizontalOverflow: document.documentElement.scrollWidth - window.innerWidth,
    };
  }, Array.from(forbiddenHeadings));

  assert.ok(result.mainTextLength > 0, `${route} at ${width}x${height} should render main content`);
  assert.ok(!result.bodyText.includes('用户名') || result.bodyText.includes('端点'), `${route} should not render the login form`);
  assert.deepEqual(result.clippedButtons, [], `${route} at ${width}x${height} should not clip button text`);
  assert.equal(result.negativeBoxes, 0, `${route} at ${width}x${height} should not render negative boxes`);
  assert.deepEqual(result.englishHeadings, [], `${route} should not expose English workbench headings`);
  assert.ok(result.horizontalOverflow <= 4, `${route} at ${width}x${height} should not overflow horizontally by ${result.horizontalOverflow}px`);

  if (route === '/settings/sync') await assertSyncPage(page);
}

async function assertSyncPage(page: Page) {
  const getCodeButton = page.locator('button', { hasText: '获取代码' });
  if (await getCodeButton.isVisible()) {
    await getCodeButton.click();
    await page.waitForFunction(
      () => !!document.querySelector('[data-testid="device-code-status"]'),
      null, { timeout: 4_000 },
    ).catch(() => undefined);
  }
  const deviceCodeWidth = await page.evaluate(() => {
    const codeEl = document.querySelector('[data-testid="device-code-status"]');
    if (!codeEl) return null;
    const style = getComputedStyle(codeEl);
    return { width: style.width, minWidth: style.minWidth };
  });
  assert.ok(deviceCodeWidth !== null, 'device-code-status element should exist');
  if (deviceCodeWidth) {
    assert.ok(deviceCodeWidth.minWidth !== '' || parseInt(String(deviceCodeWidth.width)) >= 120,
      'device-code-status should have stable width');
  }
  const discoverButton = page.locator('button', { hasText: '发现日历' });
  if (await discoverButton.isVisible()) {
    await discoverButton.click();
    await page.waitForFunction(
      () => Array.from(document.querySelectorAll('input[type="checkbox"]')).length >= 4,
      null, { timeout: 4_000 },
    ).catch(() => undefined);
  }
  const hasGroupCheckboxes = await page.evaluate(() =>
    document.querySelectorAll('input[type="checkbox"]').length >= 2);
  assert.ok(hasGroupCheckboxes, 'sync page should show grouped calendar checkboxes');
  const hasStateIndicators = await page.evaluate(() => {
    const text = document.body.textContent ?? '';
    return text.includes('只读') || text.includes('暂停') || text.includes('缺失');
  });
  assert.ok(hasStateIndicators, 'sync page should show read-only/paused/remote-missing states');
  const hasSyncControls = await page.evaluate(() => {
    const text = document.body.textContent ?? '';
    return text.includes('立即同步');
  });
  assert.ok(hasSyncControls, 'sync page should show sync controls');
  const hasDeepModes = await page.evaluate(() => {
    const text = document.body.textContent ?? '';
    return text.includes('深度同步') && text.includes('强制获取全部日程');
  });
  assert.ok(hasDeepModes, 'sync page should show deep sync and manual force-all actions');
}

// ─── Infrastructure ───────────────────────────────────────────────────

function startVite(port: number): ChildProcessWithoutNullStreams {
  const viteBin = fileURLToPath(new URL('../../src/client-web/node_modules/vite/bin/vite.js', import.meta.url));
  const child = spawn(
    process.execPath,
    [viteBin, '--host', '127.0.0.1', '--port', String(port)],
    { cwd: 'src/client-web', stdio: ['ignore', 'pipe', 'pipe'] },
  );
  child.stdout.on('data', chunk => process.stdout.write(chunk));
  child.stderr.on('data', chunk => process.stderr.write(chunk));
  return child;
}

function stopServer(server: ChildProcessWithoutNullStreams) {
  if (!server.killed) server.kill('SIGTERM');
}

async function waitForServer(baseUrl: string) {
  for (let attempt = 0; attempt < 80; attempt++) {
    try { const response = await fetch(baseUrl); if (response.ok) return; } catch { /* Vite starting */ }
    await delay(250);
  }
  throw new Error(`Timed out waiting for Vite at ${baseUrl}`);
}

async function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (!address || typeof address === 'string') { reject(new Error('Could not allocate port')); return; }
      const port = address.port;
      server.close(() => resolve(port));
    });
  });
}

function delay(ms: number) { return new Promise(resolve => setTimeout(resolve, ms)); }

// ─── Mock API ─────────────────────────────────────────────────────────

const allEvents = [
  {
    id: 'evt-outlook-1', calendarId: 'cal-outlook-1', uid: 'uid-outlook-1',
    title: 'Outlook 可编辑事件', description: 'Outlook 事件描述', location: '会议室 A',
    dtStart: '2026-07-14T09:00:00', dtEnd: '2026-07-14T10:00:00',
    status: 'confirmed', source: 'outlook',
    outlookCalendarBindingId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
    outlookEventId: 'graph-evt-001', outlookEtag: 'etag-old-001',
    outlookEventType: 'occurrence', recurrenceId: '2026-07-14T09:00:00',
    originalEventId: 'series-master-1', isAllDay: false, timeZoneId: 'Asia/Shanghai',
    categories: ['非常长的分类名称用于验证窄屏下的换行行为与抽屉边界展示效果'],
    attendees: [
      { name: '一个非常长的参会者姓名用于验证换行与抽屉边界不溢出', email: 'this-is-a-very-long-email-address-that-must-wrap-inside-the-drawer@example.com', type: 'required' },
      { name: '张小明', email: 'zhang@example.com', type: 'optional' },
    ],
    attachmentReferences: [
      { kind: 'outlook', id: 'att-outlook-1', name: '长附件名称用于验证附件行在窄屏下的适配效果.docx', contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', size: 2048, canDownload: true },
    ],
    outlookAdditionalInfo: {
      groups: [
        { key: 'meeting', label: '会议详情', items: [{ key: 'body-preview', label: '正文', value: 'PRIVATE-BODY-SECRET-MARKER' }] },
      ],
      hiddenFieldCount: 3,
    },
  },
  {
    id: 'evt-outlook-readonly-1', calendarId: 'cal-outlook-readonly', uid: 'uid-readonly-1',
    title: '只读日历 Outlook 事件',
    dtStart: '2026-07-15T14:00:00', dtEnd: '2026-07-15T15:00:00',
    status: 'confirmed', source: 'outlook',
    outlookCalendarBindingId: 'b2c3d4e5-f6a7-8901-bcde-f12345678901',
    outlookEventId: 'graph-evt-002', outlookEtag: 'etag-readonly-002',
    isAllDay: false,
  },
  {
    id: 'evt-manual-1', calendarId: 'cal-manual-1', uid: 'uid-manual-1',
    title: '手动创建的事件',
    dtStart: '2026-07-14T14:00:00+08:00', dtEnd: '2026-07-14T15:00:00+08:00',
    status: 'confirmed', source: 'manual', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  {
    id: 'evt-outlook-no-etag', calendarId: 'cal-outlook-1', uid: 'uid-no-etag',
    title: 'Outlook 无版本事件',
    dtStart: '2026-07-16T09:00:00', dtEnd: '2026-07-16T10:00:00',
    status: 'confirmed', source: 'outlook',
    outlookCalendarBindingId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
    outlookEventId: 'graph-evt-no-etag', outlookEventType: 'occurrence',
    isAllDay: false,
  },
  {
    id: 'evt-outlook-ics-1', calendarId: 'cal-ics-1', uid: 'uid-ics-1',
    title: 'Outlook ICS 导入事件',
    dtStart: '2026-07-17T09:00:00', dtEnd: '2026-07-17T10:00:00',
    status: 'confirmed', source: 'outlook-ics',
    outlookEventId: 'graph-evt-ics', outlookEtag: 'etag-ics',
    isAllDay: false,
  },
  {
    id: 'evt-outlook-single-1', calendarId: 'cal-outlook-1', uid: 'uid-single-1',
    title: 'Outlook 单实例事件',
    dtStart: '2026-07-18T09:00:00', dtEnd: '2026-07-18T10:00:00',
    status: 'confirmed', source: 'outlook',
    outlookCalendarBindingId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
    outlookEventId: 'graph-evt-single', outlookEtag: 'etag-single-1',
    isAllDay: false,
  },
  {
    id: 'evt-outlook-master-1', calendarId: 'cal-outlook-1', uid: 'uid-master-1',
    title: 'Outlook 系列主事件',
    dtStart: '2026-07-19T09:00:00', dtEnd: '2026-07-19T10:00:00',
    status: 'confirmed', source: 'outlook',
    outlookCalendarBindingId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
    outlookEventId: 'graph-evt-master', outlookEtag: 'etag-master-1',
    outlookEventType: 'seriesMaster', isAllDay: false,
  },
  {
    id: 'evt-outlook-exception-1', calendarId: 'cal-outlook-1', uid: 'uid-exception-1',
    title: 'Outlook 例外事件',
    dtStart: '2026-07-20T09:00:00', dtEnd: '2026-07-20T10:00:00',
    status: 'confirmed', source: 'outlook',
    outlookCalendarBindingId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
    outlookEventId: 'graph-evt-exception', outlookEtag: 'etag-exception-1',
    outlookEventType: 'exception', isAllDay: false,
  },
  {
    id: 'evt-html-desc-1', calendarId: 'cal-manual-1', uid: 'uid-html-1',
    title: 'HTML 描述事件',
    description: '<p>描述包含 <b>HTML</b> 内容</p><div>额外 div 内容</div><script>window.__pimHtmlExecuted = true</script><img src="x" onerror="window.__pimHtmlExecuted = true">',
    dtStart: '2026-07-21T09:00:00', dtEnd: '2026-07-21T10:00:00',
    status: 'confirmed', source: 'outlook', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  // ── Scenario K fixtures ──────────────────────────────────────────
  {
    id: 'evt-density-detail', calendarId: 'cal-manual-1', uid: 'uid-density-detail',
    title: '密度详情事件', location: '会议室 A',
    description: '这是一个包含详细描述的事件，用于测试日历卡片的层级展示功能。描述内容需要足够长以确保在层级 4 能够显示摘要。这里继续添加更多文本内容以增加描述的长度。',
    dtStart: '2026-07-20T09:00:00+08:00', dtEnd: '2026-07-20T12:00:00+08:00',
    status: 'confirmed', source: 'manual', rrule: 'FREQ=WEEKLY', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  {
    id: 'evt-density-compact', calendarId: 'cal-manual-1', uid: 'uid-density-compact',
    title: '密度紧凑事件', location: '隐藏的位置', description: '隐藏的描述',
    dtStart: '2026-07-20T13:00:00+08:00', dtEnd: '2026-07-20T13:15:00+08:00',
    status: 'confirmed', source: 'manual', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  {
    id: 'evt-capacity-1', calendarId: 'cal-manual-1', uid: 'uid-capacity-1',
    title: '容量日程 1',
    dtStart: '2026-07-15T09:00:00+08:00', dtEnd: '2026-07-15T10:00:00+08:00',
    status: 'confirmed', source: 'manual', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  {
    id: 'evt-capacity-2', calendarId: 'cal-manual-1', uid: 'uid-capacity-2',
    title: '容量日程 2',
    dtStart: '2026-07-15T10:00:00+08:00', dtEnd: '2026-07-15T11:00:00+08:00',
    status: 'confirmed', source: 'manual', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  {
    id: 'evt-capacity-3', calendarId: 'cal-manual-1', uid: 'uid-capacity-3',
    title: '容量日程 3',
    dtStart: '2026-07-15T11:00:00+08:00', dtEnd: '2026-07-15T12:00:00+08:00',
    status: 'confirmed', source: 'manual', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  {
    id: 'evt-capacity-4', calendarId: 'cal-manual-1', uid: 'uid-capacity-4',
    title: '容量日程 4',
    dtStart: '2026-07-15T12:00:00+08:00', dtEnd: '2026-07-15T13:00:00+08:00',
    status: 'confirmed', source: 'manual', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  {
    id: 'evt-capacity-5', calendarId: 'cal-manual-1', uid: 'uid-capacity-5',
    title: '容量日程 5',
    dtStart: '2026-07-15T13:00:00+08:00', dtEnd: '2026-07-15T14:00:00+08:00',
    status: 'confirmed', source: 'manual', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  {
    id: 'evt-local-tz', calendarId: 'cal-manual-1', uid: 'uid-local-tz',
    title: '本地时区验证事件',
    dtStart: '2026-07-20T14:00:00Z', dtEnd: '2026-07-20T15:00:00Z',
    status: 'confirmed', source: 'manual', isAllDay: false,
  },
  {
    id: 'evt-title-priority', calendarId: 'cal-manual-1', uid: 'uid-title-priority',
    title: '超长标题事件用于验证日历卡片标题优先级与省略号显示行为必须完整测试',
    dtStart: '2026-07-16T11:00:00+08:00', dtEnd: '2026-07-16T12:00:00+08:00',
    status: 'confirmed', source: 'manual', isAllDay: false, timeZoneId: 'Asia/Shanghai',
  },
  // ── Task 8 calendar-semantics fixtures ─────────────────────────────
  {
    id: 'evt-semantic-free', calendarId: 'cal-outlook-1', uid: 'uid-semantic-free',
    title: '重要空闲事件', location: '线上会议室',
    description: '这个事件用于验证 importance=high 与 showAs=free 的视觉语义。描述内容足够长以撑起时间轴卡片的最高内容层级，从而显示重要与提醒图标。',
    dtStart: '2026-07-20T09:00:00+08:00', dtEnd: '2026-07-20T12:00:00+08:00',
    status: 'confirmed', source: 'outlook', isAllDay: false, timeZoneId: 'Asia/Shanghai',
    importance: 'high', showAs: 'free', isReminderOn: true, reminderMinutesBeforeStart: 15,
  },
  {
    id: 'evt-semantic-tentative', calendarId: 'cal-outlook-1', uid: 'uid-semantic-tentative',
    title: '暂定事件', location: '待定会议室',
    description: '这个事件用于验证 showAs=tentative 的暂定视觉语义。描述内容足够长以撑起时间轴卡片的最高内容层级。',
    dtStart: '2026-07-20T13:00:00+08:00', dtEnd: '2026-07-20T16:00:00+08:00',
    status: 'confirmed', source: 'outlook', isAllDay: false, timeZoneId: 'Asia/Shanghai',
    showAs: 'tentative',
  },
];

const calendars = [
  { id: 'cal-outlook-1', name: 'Outlook 工作日历', color: '#0044CC', kind: 'calendar', isDefault: false, outlookCalendarBindingId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890', canEdit: true },
  { id: 'cal-outlook-readonly', name: 'Outlook 只读日历', color: '#00AA44', kind: 'calendar', isDefault: false, outlookCalendarBindingId: 'b2c3d4e5-f6a7-8901-bcde-f12345678901', canEdit: false },
  { id: 'cal-manual-1', name: '本地日历', color: '#AA4400', kind: 'calendar', isDefault: true, canEdit: true },
  { id: 'cal-ics-1', name: 'ICS 导入日历', color: '#6633CC', kind: 'calendar', isDefault: false, canEdit: true },
];

const allTasks = [
  {
    id: 'task-audit-1',
    calendarId: 'cal-manual-1',
    title: '排程任务',
    description: '测试描述',
    priority: 1,
    estimatedDuration: '01:30:00',
    minimumSegment: null,
    dtStart: '2026-07-14T06:00:00Z',
    plannedEnd: '2026-07-14T07:30:00Z',
    due: '2026-07-15T04:00:00Z',
    status: 'NEEDS-ACTION',
    isInbox: false,
    sortOrder: 1,
    subTasks: [],
  },
  {
    id: 'task-audit-2',
    calendarId: 'cal-manual-1',
    title: '无预估时长任务',
    description: null,
    priority: 0,
    estimatedDuration: null,
    minimumSegment: null,
    dtStart: '2026-07-15T08:00:00Z',
    plannedEnd: '2026-07-15T09:30:00Z',
    due: '2026-07-16T06:00:00Z',
    status: 'NEEDS-ACTION',
    isInbox: false,
    sortOrder: 0,
    subTasks: [],
  },
  {
    id: 'task-created-1',
    calendarId: 'cal-manual-1',
    title: '新建任务可靠性测试',
    description: null,
    priority: 0,
    estimatedDuration: '02:15:00',
    minimumSegment: null,
    dtStart: '2026-07-16T01:00:00Z',
    plannedEnd: '2026-07-16T03:15:00Z',
    due: '2026-07-17T10:00:00Z',
    status: 'NEEDS-ACTION',
    isInbox: false,
    sortOrder: 0,
    subTasks: [],
  },
];

function mockApiResponse(
  fullPath: string,
  method?: string,
  includeScenarioKFixtures = false,
): { code: number; message: string; data: unknown; timestamp: string } {
  const eventsForScenario = allEvents.filter(event => {
    const isScenarioKFixture = event.id.startsWith('evt-density-')
      || event.id.startsWith('evt-capacity-')
      || event.id === 'evt-local-tz'
      || event.id.startsWith('evt-semantic-');
    return includeScenarioKFixtures ? isScenarioKFixture : !isScenarioKFixture;
  });
  let data: unknown = [];
  if (fullPath.endsWith('/status/summary')) {
    data = { status: 'Healthy', checks: [] };
  } else if (fullPath.includes('/calendar/data-center/query')) {
    data = { items: [], page: 1, pageSize: 50, totalCount: 0 };
  } else if (fullPath.includes('/calendar/outlook/settings')) {
    data = { provider: 'outlook', tenantId: 'common', clientId: '11111111-1111-1111-1111-111111111111', scopes: 'Calendars.ReadWrite offline_access', status: 'connected', tokenHealth: 'Healthy', lastSyncedAt: '2026-07-13T08:00:00Z', lastError: null, uiStatus: 'connected', activeAuthorization: { id: 'auth-1', status: 'connected', verificationUri: 'https://microsoft.com/devicelogin', userCode: 'ABC123XYZ', expiresAt: '2026-07-13T12:00:00Z', accountDisplayName: 'Test User', accountLoginHint: 'user@example.com', errorCode: null, errorMessage: null, recoveryAction: null } };
  } else if (fullPath.includes('/calendar/outlook/device-code')) {
    if (fullPath.endsWith('/poll')) {
      data = { id: 'd4e5f6a7-b8c9-0123-defa-234567890123', status: 'connected', verificationUri: 'https://microsoft.com/devicelogin', userCode: 'ABC123XYZ', expiresAt: '2026-07-13T12:00:00Z', accountDisplayName: 'Test User', accountLoginHint: 'user@example.com', errorCode: null, errorMessage: null, recoveryAction: null };
    } else if (fullPath.endsWith('/cancel')) {
      data = 'cancelled';
    } else {
      data = { id: 'd4e5f6a7-b8c9-0123-defa-234567890123', status: 'waiting-for-user', verificationUri: 'https://microsoft.com/devicelogin', userCode: 'ABC123XYZ', expiresAt: '2026-07-13T12:00:00Z', accountDisplayName: null, accountLoginHint: null, errorCode: null, errorMessage: null, recoveryAction: null };
    }
  } else if (fullPath.includes('/calendar/outlook/calendars/discover')) {
    data = [
      { id: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890', pimCalendarId: 'p1m2c3d4-e5f6-7890-abcd-ef1234567890', graphCalendarId: 'graph-cal-1', groupId: null, groupName: 'Work', name: '工作日历', color: '#0044CC', ownerName: null, ownerAddress: null, isDefault: true, canEdit: true, isSelected: true, remoteState: 'active', lastSyncedAt: '2026-07-13T08:00:00Z', lastError: null },
      { id: 'b2c3d4e5-f6a7-8901-bcde-f12345678901', pimCalendarId: 'q2r3s4t5-u6v7-8901-bcde-f12345678901', graphCalendarId: 'graph-cal-2', groupId: null, groupName: 'Personal', name: '个人日历', color: '#00AA44', ownerName: null, ownerAddress: null, isDefault: false, canEdit: false, isSelected: false, remoteState: 'active', lastSyncedAt: null, lastError: null },
      { id: 'c3d4e5f6-a7b8-9012-cdef-123456789012', pimCalendarId: 'r3s4t5u6-v7w8-9012-cdef-123456789012', graphCalendarId: 'graph-cal-3', groupId: null, groupName: 'Work', name: '团队日历', color: '#AA4400', ownerName: null, ownerAddress: null, isDefault: false, canEdit: true, isSelected: false, remoteState: 'paused', lastSyncedAt: '2026-07-10T08:00:00Z', lastError: null },
      { id: 'd4e5f6a7-b8c9-0123-defa-234567890123', pimCalendarId: 's4t5u6v7-w8x9-0123-defa-234567890123', graphCalendarId: 'graph-cal-4', groupId: null, groupName: null, name: '已删除日历', color: '#888888', ownerName: null, ownerAddress: null, isDefault: false, canEdit: false, isSelected: false, remoteState: 'remote-missing', lastSyncedAt: null, lastError: null },
    ];
  } else if (fullPath.includes('/calendar/outlook/calendars/selection')) {
    data = [
      { id: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890', pimCalendarId: 'p1m2c3d4-e5f6-7890-abcd-ef1234567890', graphCalendarId: 'graph-cal-1', groupId: null, groupName: 'Work', name: '工作日历', color: '#0044CC', ownerName: null, ownerAddress: null, isDefault: true, canEdit: true, isSelected: true, remoteState: 'active', lastSyncedAt: '2026-07-13T08:00:00Z', lastError: null },
      { id: 'c3d4e5f6-a7b8-9012-cdef-123456789012', pimCalendarId: 'r3s4t5u6-v7w8-9012-cdef-123456789012', graphCalendarId: 'graph-cal-3', groupId: null, groupName: 'Work', name: '团队日历', color: '#AA4400', ownerName: null, ownerAddress: null, isDefault: false, canEdit: true, isSelected: true, remoteState: 'paused', lastSyncedAt: '2026-07-10T08:00:00Z', lastError: null },
    ];
  } else if (fullPath.includes('/calendar/outlook/sync/batches')) {
    const batchItems = [{
      id: 'e5f6a7b8-c9d0-1234-efab-345678901234', provider: 'outlook', status: 'completed', readCount: 50, createdCount: 2, updatedCount: 5, conflictCount: 0, confirmationCount: 0, failureCount: 1, steps: [], errorSummary: null,
      startedAt: '2026-07-13T08:00:00Z', finishedAt: '2026-07-13T08:02:00Z', mode: 'normal', requestedWindowStart: null, requestedWindowEnd: null,
      perCalendarJson: JSON.stringify([
        { bindingId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890', calendarName: '工作日历', status: 'completed', readCount: 50, createdCount: 2, updatedCount: 5, deletedCount: 0, failureCount: 0, changes: [{ id: 'evt-10', title: 'Sync Event', action: 'created' }], failures: [] },
        { bindingId: 'd4e5f6a7-b8c9-0123-defa-234567890123', calendarName: '已删除日历', status: 'failed', readCount: 0, createdCount: 0, updatedCount: 0, deletedCount: 0, failureCount: 1, changes: [], failures: [{ eventId: 'evt-1', title: 'Meeting', code: 'AuthError', message: 'Permission denied' }] },
      ]),
      cancelRequested: false,
    }];
    data = { items: batchItems, total: 1, page: 1, pageSize: 20 };
  } else if (fullPath.includes('/calendar/outlook/sync') && !fullPath.includes('/batches')) {
    if (fullPath.endsWith('/cancel')) {
      data = 'cancelled';
    } else {
      data = { id: 'f6a7b8c9-d0e1-2345-fabc-456789012345', provider: 'outlook', status: 'running', readCount: 0, createdCount: 0, updatedCount: 0, conflictCount: 0, confirmationCount: 0, failureCount: 0, steps: [], errorSummary: null, startedAt: '2026-07-13T09:00:00Z', finishedAt: null, mode: 'normal', requestedWindowStart: null, requestedWindowEnd: null, perCalendarJson: null, cancelRequested: false };
    }
  } else if (fullPath.includes('/calendar/outlook/local-data/preview')) {
    data = { bindingCount: 3, calendarCount: 5, eventCount: 120 };
  } else if (fullPath.includes('/calendar/outlook/local-data')) {
    data = 'deleted';
  } else if (fullPath.includes('/calendar/outlook/disconnect')) {
    data = 'disconnected';
  } else if (fullPath.includes('/calendar/outlook/check')) {
    data = { provider: 'outlook', tenantId: 'common', clientId: '11111111-1111-1111-1111-111111111111', scopes: 'Calendars.ReadWrite offline_access', status: 'connected', tokenHealth: 'healthy', lastSyncedAt: '2026-07-13T08:00:00Z', lastError: null, uiStatus: 'connected', activeAuthorization: null };
  } else if (fullPath.match(/\/calendar\/events\/[^/]+$/)) {
    if (method === 'PUT' || method === 'DELETE') {
      const evtId = fullPath.split('/').pop()?.split('?')[0];
      const evt = allEvents.find(e => e.id === evtId) || allEvents[0];
      data = method === 'DELETE' ? null : { ...evt, title: (evt as { title?: string }).title ? `${(evt as { title?: string }).title} (updated)` : 'updated' };
    } else {
      const evtId = fullPath.split('/').pop()?.split('?')[0];
      data = allEvents.find(e => e.id === evtId) || allEvents[0];
    }
  } else if (fullPath.includes('/calendar/events')) {
    data = eventsForScenario;
  } else if (fullPath.match(/\/calendar\/tasks\/[^/]+-[^/]+$/)) {
    const taskId = fullPath.split('/').pop()?.split('?')[0] || '';
    if (method === 'PUT') {
      data = allTasks.find(t => t.id === taskId) || allTasks[0];
    } else if (method === 'DELETE') {
      data = null;
    } else {
      data = allTasks.find(t => t.id === taskId) || allTasks[0];
    }
  } else if (fullPath.match(/\/calendar\/tasks(\?|$)/)) {
    if (method === 'POST') {
      data = allTasks.find(t => t.id === 'task-created-1') || allTasks[0];
    } else {
      data = includeScenarioKFixtures ? [] : allTasks;
    }
  } else if (fullPath.includes('/calendar/calendars') && !fullPath.includes('outlook')) {
    data = calendars;
  } else if (fullPath.includes('/calendar/outlook/events/writeback')) {
    data = { status: 'created' };
  } else if (fullPath.includes('/operations/audit/')) {
    data = { items: [] };
  } else if (fullPath.includes('/endpoints/') && fullPath.endsWith('/collection-quality')) {
    data = { deviceId: 'windows-companion', platform: 'windows', uploadStatus: 'Healthy', issueCount: 0, checkedAt: new Date().toISOString() };
  } else if (fullPath.includes('/today/sections')) {
    data = [];
  }
  return { code: 0, message: 'OK', data, timestamp: new Date().toISOString() };
}

main().catch((error: unknown) => {
  console.error(error);
  process.exit(1);
});

// ─── Helpers ──────────────────────────────────────────────────────────

async function openCalendarMonth(page: Page, baseUrl: string) {
  // Fixtures are intentionally fixed in July 2026; stabilize the calendar's
  // production-default "today" without changing the real initial date.
  await setScenarioKClock(page);
  await page.goto(`${baseUrl}/calendar?view=month`, { waitUntil: 'domcontentloaded' });
  await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined);
  await page.waitForSelector('.fc-event, .calendar-event-card', { timeout: 8_000 }).catch(() => undefined);
}

async function openEventByText(page: Page, text: string, force = false) {
  let evt = page.locator('.fc-event').filter({ hasText: text }).first();
  await evt.waitFor({ state: 'attached', timeout: 8_000 });
  if (!await evt.isVisible()) {
    const dayCell = evt.locator('xpath=ancestor::*[contains(@class, "fc-daygrid-day")][1]');
    const moreLink = dayCell.locator('.fc-more-link');
    await moreLink.waitFor({ state: 'visible', timeout: 5_000 });
    await moreLink.click();
    const popover = page.locator('.fc-more-popover');
    await popover.waitFor({ state: 'visible', timeout: 3_000 });
    evt = popover.locator('.fc-event').filter({ hasText: text }).first();
  }
  await evt.waitFor({ state: 'visible', timeout: 8_000 });
  if (force) {
    await evt.dispatchEvent('click');
  } else {
    await evt.click();
  }
  await page.waitForSelector('aside[role="dialog"]', { timeout: 5_000 });
}

async function waitForWritebackPreview(page: Page) {
  await page.waitForFunction(
    () => document.body.textContent?.includes('Outlook 写回 确认'),
    null, { timeout: 5_000 },
  );
}

async function waitForConflict(page: Page) {
  await page.waitForFunction(
    () => document.body.textContent?.includes('变更冲突'),
    null, { timeout: 5_000 },
  );
}

function writebackConfirmBtn(page: Page) {
  return page.locator('div[role="dialog"][aria-modal="true"] footer button', { hasText: /确认|提交中/ });
}

function latestOutlookBlock(page: Page) {
  return page
    .locator('div[role="dialog"][aria-modal="true"]')
    .getByText('最新 Outlook 内容')
    .locator('..');
}

function latestConflictEvent(title: string): unknown {
  return {
    id: 'evt-outlook-1',
    calendarId: 'cal-outlook-1',
    uid: 'uid-outlook-1',
    title,
    description: 'Outlook 事件描述',
    location: '会议室 A',
    dtStart: '2026-07-14T09:30:00',
    dtEnd: '2026-07-14T10:30:00',
    status: 'confirmed',
    source: 'outlook',
    outlookCalendarBindingId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
    outlookEventId: 'graph-evt-001',
    outlookEtag: 'latest-etag',
    outlookEventType: 'occurrence',
    isAllDay: false,
    timeZoneId: 'Asia/Shanghai',
    categories: ['非常长的分类名称用于验证窄屏下的换行行为与抽屉边界展示效果'],
    attendees: [
      { name: '一个非常长的参会者姓名用于验证换行与抽屉边界不溢出', email: 'this-is-a-very-long-email-address-that-must-wrap-inside-the-drawer@example.com', type: 'required' },
    ],
    attachmentReferences: [
      { kind: 'outlook', id: 'att-outlook-1', name: '长附件名称用于验证附件行在窄屏下的适配效果.docx', contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', size: 2048, canDownload: true },
    ],
  };
}

function conflictResponse(title: string, etag: string): { code: number; message: string; data: unknown; timestamp: string } {
  return {
    code: 0,
    message: 'Conflict',
    data: {
      status: 'conflict',
      latestOutlookJson: JSON.stringify({
        id: 'graph-evt-001', subject: title,
        start: { dateTime: '2026-07-14T09:30:00', timeZone: 'Asia/Shanghai' },
        end: { dateTime: '2026-07-14T10:30:00', timeZone: 'Asia/Shanghai' },
      }),
      latestEvent: latestConflictEvent(title),
      latestEtag: etag,
    },
    timestamp: new Date().toISOString(),
  };
}

async function waitForNoWritebackDialog(page: Page) {
  await page.waitForFunction(
    () => !document.querySelector('div[role="dialog"][aria-modal="true"]'),
    null, { timeout: 5_000 },
  );
}

// ─── Scenario A: Manual and outlook-ics isolation ─────────────────────

async function runScenarioA(browser: Browser, baseUrl: string) {
  const [w, h] = viewports[2];
  const context = await newFixedContext(browser, {
    viewport: { width: w, height: h },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  try {
    const captured: CapturedRequest[] = [];
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
    });
    // Generic route (LIFO lower)
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      const method = route.request().method();
      if (method !== 'GET') {
        const postBody = route.request().postDataJSON();
        captured.push({ url: fullPath, method, body: postBody });
      } else { captured.push({ url: fullPath, method }); }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, method)),
      });
    });
    // Specific writeback route (LIFO higher)
    await context.route('**/api/v1/calendar/outlook/events/writeback', async route => {
      const body = route.request().postDataJSON();
      captured.push({ url: 'writeback', method: 'POST', body });
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ code: 0, message: 'OK',
          data: { status: 'updated' }, timestamp: new Date().toISOString() }),
      });
    });

    const page = await context.newPage();
    await openCalendarMonth(page, baseUrl);

    // A1: Open manual event, edit, save — assert PUT with no writeback
    await openEventByText(page, '手动创建的事件');
    const titleInput = page.locator('aside[role="dialog"] input[type="text"]').first();
    await titleInput.fill('手动事件已编辑');
    await page.locator('aside[role="dialog"] button[type="submit"]').click();

    // Wait for editor to close (no writeback dialog should appear)
    await page.waitForFunction(
      () => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 5_000 },
    ).catch(() => undefined);

    const wbCalls = captured.filter(c => c.url === 'writeback' || c.url.includes('writeback'));
    assert.equal(wbCalls.length, 0, 'Manual event must not trigger Outlook writeback');

    const putCalls = captured.filter(c => c.method === 'PUT' && c.url.includes('/calendar/events/'));
    assert.ok(putCalls.length >= 1, 'Manual event must trigger PUT to calendar events');
    const putUrl = putCalls[0].url;
    assert.ok(putUrl.includes('evt-manual-1'), 'PUT must target manual event');

    // A2: Open outlook-ics event, edit, save — PUT with no writeback
    await openEventByText(page, 'Outlook ICS 导入事件');
    const icsTitleInput = page.locator('aside[role="dialog"] input[type="text"]').first();
    await icsTitleInput.fill('ICS 导入事件已编辑');
    await page.locator('aside[role="dialog"] button[type="submit"]').click();

    await page.waitForFunction(
      () => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 5_000 },
    ).catch(() => undefined);

    assert.equal(captured.filter(c => c.url === 'writeback' || c.url.includes('writeback')).length, 0,
      'outlook-ics event must not trigger Outlook writeback');

    const icsPut = captured.find(c => c.method === 'PUT' && c.url.includes('evt-outlook-ics-1'));
    assert.ok(icsPut, 'outlook-ics event must trigger PUT targeting evt-outlook-ics-1');

    await page.close();
  } finally {
    await context.close();
  }
}

// ─── Scenario B: Outlook update and conflict ──────────────────────────

async function runScenarioB(browser: Browser, baseUrl: string) {
  const [w, h] = viewports[2];
  const context = await newFixedContext(browser, {
    viewport: { width: w, height: h },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  let conflictPage: Page | undefined;
  try {
    const captured: CapturedRequest[] = [];
    let conflictSeq = 0;
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
    });
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      const method = route.request().method();
      if (method !== 'GET') {
        const postBody = route.request().postDataJSON();
        captured.push({ url: fullPath, method, body: postBody });
      }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, method)),
      });
    });
    await context.route('**/api/v1/calendar/outlook/events/writeback', async route => {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      captured.push({ url: 'writeback', method: 'POST', body });
      conflictSeq++;
      if (conflictSeq === 1) {
        return route.fulfill({
          status: 409, contentType: 'application/json',
          body: JSON.stringify(conflictResponse('Outlook Updated Title', 'etag-new-001')),
        });
      }
      if (conflictSeq === 2) {
        return route.fulfill({
          status: 409, contentType: 'application/json',
          body: JSON.stringify(conflictResponse('Another Outlook Update', 'etag-new-002')),
        });
      }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ code: 0, message: 'OK',
          data: { status: 'updated' }, timestamp: new Date().toISOString() }),
      });
    });

    conflictPage = await context.newPage();
    await openCalendarMonth(conflictPage, baseUrl);

    // Open editable Outlook event
    await openEventByText(conflictPage, 'Outlook 可编辑事件');

    // Edit title and save to enter writeback preview
    const titleInput = conflictPage.locator('aside[role="dialog"] input[type="text"]').first();
    await titleInput.fill('编辑后标题');
    await conflictPage.locator('aside[role="dialog"] button[type="submit"]').click();

    await waitForWritebackPreview(conflictPage);

    // Task 8: the preview must be a typed field-level diff, never raw JSON
    const baSection = conflictPage.locator('section[aria-label="变更前后对比"]');
    await baSection.waitFor({ state: 'visible', timeout: 3_000 });
    assert.equal(
      await conflictPage.locator('div[role="dialog"][aria-modal="true"] pre').count(),
      0,
      'Writeback dialog must not render raw JSON <pre>',
    );
    const titleRow = baSection.locator('li[aria-label^="标题：修改"]');
    await titleRow.waitFor({ state: 'visible', timeout: 3_000 });
    assert.ok(
      (await titleRow.getAttribute('aria-label'))?.includes('Outlook 可编辑事件 → 编辑后标题'),
      'Typed title row must read 旧标题 → 新标题',
    );
    assert.equal(await baSection.locator('li').count(), 1,
      'Unchanged fields must be omitted from the typed diff');
    const metaText = await baSection.locator('dl').textContent();
    assert.ok(metaText?.includes('操作'), 'Diff meta must show 操作');
    assert.ok(metaText?.includes('更新'), 'Diff meta must show the operation label');
    assert.ok(metaText?.includes('账户') && metaText.includes('Test User'), 'Diff meta must show the account name');
    assert.ok(metaText?.includes('日历') && metaText.includes('Outlook 工作日历'), 'Diff meta must show the calendar name');
    assert.ok(metaText?.includes('范围') && metaText.includes('仅此实例'), 'Diff meta must show the scope');

    // Verify scope radio visible in writeback dialog for occurrence type
    const instanceRadio = conflictPage.locator('input[name="outlook-scope"][value="instance"]');
    await instanceRadio.waitFor({ state: 'visible', timeout: 3_000 });

    // No writeback request before confirmation
    assert.equal(captured.filter(c => c.url === 'writeback').length, 0,
      'No writeback request before preview confirmation');

    // Select series scope, then confirm -> first 409
    const seriesRadio = conflictPage.locator('input[name="outlook-scope"][value="series"]');
    await seriesRadio.waitFor({ state: 'visible', timeout: 3_000 });
    await seriesRadio.click();
    await writebackConfirmBtn(conflictPage).click();

    await waitForConflict(conflictPage);

    // First request payload checks
    const wbAfterFirst = captured.filter(c => c.url === 'writeback');
    assert.equal(wbAfterFirst.length, 1, 'Exactly one writeback request after first confirm');
    const p1 = wbAfterFirst[0].body as Record<string, unknown>;
    assert.equal(p1.operation, 'update');
    assert.equal(p1.eventId, 'evt-outlook-1');
    assert.equal(p1.scope, 'series');
    assert.equal(p1.expectedEtag, 'etag-old-001');
    assert.ok(p1.clientOperationId && typeof p1.clientOperationId === 'string');
    const firstOpId = p1.clientOperationId as string;
    assert.ok(firstOpId.length > 0);
    if (p1.draft) assert.equal((p1.draft as Record<string, unknown>).title, '编辑后标题');

    // Latest Outlook content visible as a typed summary, not raw JSON
    assert.ok(await conflictPage.locator('text=最新 Outlook 内容').isVisible().catch(() => false),
      'Latest Outlook content should be shown');
    const latestBlock = latestOutlookBlock(conflictPage);
    await latestBlock.getByText('Outlook Updated Title').waitFor({ state: 'visible', timeout: 3_000 });
    assert.equal(
      await conflictPage.locator('div[role="dialog"][aria-modal="true"] pre').count(),
      0,
      'Conflict must not render raw Outlook JSON',
    );
    const draftVal = await titleInput.inputValue().catch(() => '');
    assert.ok(draftVal.includes('编辑后标题'), 'Edited draft preserved');

    // Click "重新比较" -> no request sent, back to preview
    const retryBtn = conflictPage.locator('button', { hasText: '重新比较' });
    await retryBtn.waitFor({ state: 'visible', timeout: 3_000 });
    const seqBefore = captured.filter(c => c.url === 'writeback').length;
    await retryBtn.click();
    await waitForWritebackPreview(conflictPage);
    assert.equal(captured.filter(c => c.url === 'writeback').length, seqBefore,
      'No request after retry click');

    // After recompare the diff compares the draft against the typed latest
    const recompareTitleRow = conflictPage.locator('section[aria-label="变更前后对比"] li[aria-label^="标题："]');
    await recompareTitleRow.waitFor({ state: 'visible', timeout: 3_000 });
    const recompareLabel = await recompareTitleRow.getAttribute('aria-label');
    assert.ok(
      recompareLabel?.includes('Outlook Updated Title → 编辑后标题'),
      `Recompare row must diff latest title against draft, got ${recompareLabel}`,
    );

    // Second confirmation -> second 409
    await writebackConfirmBtn(conflictPage).click();
    await waitForConflict(conflictPage);

    const wbAfterSecond = captured.filter(c => c.url === 'writeback');
    assert.equal(wbAfterSecond.length, 2, 'Exactly two writeback requests');
    const p2 = wbAfterSecond[1].body as Record<string, unknown>;
    assert.equal(p2.expectedEtag, 'etag-new-001', 'Second request uses latestEtag');
    assert.equal(p2.eventId, 'evt-outlook-1');
    assert.equal(p2.scope, 'series');
    assert.equal(p2.clientOperationId, firstOpId, 'clientOperationId persists');

    await latestOutlookBlock(conflictPage)
      .getByText('Another Outlook Update')
      .waitFor({ state: 'visible', timeout: 5_000 });
    assert.equal(captured.filter(c => c.url === 'writeback').length, 2,
      'Repeated conflict must not trigger an automatic third request');

    // Cancel the conflict back to the editor: edits must not be lost
    await conflictPage.locator('div[role="dialog"][aria-modal="true"] button', { hasText: '取消' }).click();
    const editorAfterCancel = await conflictPage.locator('aside[role="dialog"]').isVisible({ timeout: 3_000 }).catch(() => false);
    assert.ok(editorAfterCancel, 'Editor must stay open after canceling the conflict dialog');
    const draftAfterCancel = await titleInput.inputValue().catch(() => '');
    assert.ok(draftAfterCancel.includes('编辑后标题'), 'Draft must survive canceling the conflict dialog');

    await conflictPage.close();
    conflictPage = undefined;
  } finally {
    if (conflictPage) await conflictPage.close().catch(() => undefined);
    await context.close();
  }
}

// ─── Scenario C: Outlook delete ──────────────────────────────────────

async function runScenarioC(browser: Browser, baseUrl: string) {
  const [w, h] = viewports[2];
  const context = await newFixedContext(browser, {
    viewport: { width: w, height: h },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  let deletePage: Page | undefined;
  try {
    const captured: CapturedRequest[] = [];
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
    });
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      const method = route.request().method();
      if (method !== 'GET') {
        const postBody = route.request().postDataJSON();
        captured.push({ url: fullPath, method, body: postBody });
      }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, method)),
      });
    });
    await context.route('**/api/v1/calendar/outlook/events/writeback', async route => {
      const body = route.request().postDataJSON();
      captured.push({ url: 'writeback', method: 'POST', body });
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ code: 0, message: 'OK',
          data: { status: 'deleted' }, timestamp: new Date().toISOString() }),
      });
    });

    deletePage = await context.newPage();
    await openCalendarMonth(deletePage, baseUrl);
    await openEventByText(deletePage, 'Outlook 可编辑事件');

    // Click delete
    const deleteBtn = deletePage.locator('aside[role="dialog"] button', { hasText: '删除' });
    await deleteBtn.waitFor({ state: 'visible', timeout: 3_000 });
    const wbBefore = captured.filter(c => c.url === 'writeback').length;
    await deleteBtn.click();

    await waitForWritebackPreview(deletePage);
    await deletePage
      .locator('div[role="dialog"][aria-modal="true"] h2', { hasText: '删除 Outlook 日程' })
      .waitFor({ state: 'visible', timeout: 3_000 });
    assert.equal(captured.filter(c => c.url === 'writeback').length, wbBefore,
      'No writeback request before confirming delete');

    // Confirm deletion
    await writebackConfirmBtn(deletePage).click();
    await waitForNoWritebackDialog(deletePage);

    const wbCalls = captured.filter(c => c.url === 'writeback');
    assert.equal(wbCalls.length, 1, 'Exactly one writeback request for delete');
    const payload = wbCalls[0].body as Record<string, unknown>;
    assert.equal(payload.operation, 'delete');
    assert.equal(payload.eventId, 'evt-outlook-1');
    assert.ok(payload.calendarBindingId);
    assert.equal(payload.scope, 'instance');
    assert.ok(payload.expectedEtag);
    assert.ok(!payload.draft, 'Delete must not include draft');

    await deletePage.close();
    deletePage = undefined;
  } finally {
    if (deletePage) await deletePage.close().catch(() => undefined);
    await context.close();
  }
}

// ─── Scenario D: Outlook create ───────────────────────────────────────

async function runScenarioD(browser: Browser, baseUrl: string) {
  const [w, h] = viewports[2];
  const context = await newFixedContext(browser, {
    viewport: { width: w, height: h },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  let createPage: Page | undefined;
  try {
    const captured: CapturedRequest[] = [];
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
    });
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      const method = route.request().method();
      if (method !== 'GET') {
        const postBody = route.request().postDataJSON();
        captured.push({ url: fullPath, method, body: postBody });
      }
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, method)),
      });
    });
    await context.route('**/api/v1/calendar/outlook/events/writeback', async route => {
      const body = route.request().postDataJSON();
      captured.push({ url: 'writeback', method: 'POST', body });
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ code: 0, message: 'OK',
          data: { status: 'created' }, timestamp: new Date().toISOString() }),
      });
    });

    createPage = await context.newPage();
    await createPage.goto(`${baseUrl}/calendar?view=month`, { waitUntil: 'domcontentloaded' });
    await createPage.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined);

    const inboxPanel = createPage.getByRole('heading', { name: '收集箱', exact: true }).locator('../..');
    const addBtn = inboxPanel.getByRole('button', { name: '+ 新建', exact: true });
    await addBtn.waitFor({ state: 'visible', timeout: 5_000 });
    await addBtn.click();
    const scheduleOption = inboxPanel.getByRole('button', { name: '日程', exact: true });
    await scheduleOption.waitFor({ state: 'visible', timeout: 3_000 });
    await scheduleOption.click();
    await createPage
      .locator('aside[role="dialog"] h2', { hasText: '新建日程' })
      .waitFor({ state: 'visible', timeout: 5_000 });

    // Select Outlook calendar
    const calSelect = createPage.locator('aside[role="dialog"] select').first();
    await calSelect.waitFor({ state: 'visible', timeout: 3_000 });
    await calSelect.selectOption({ label: 'Outlook 工作日历 (Outlook)' });

    // Fill fields
    const titleInput = createPage.locator('aside[role="dialog"] input[type="text"]').first();
    await titleInput.fill('新建 Outlook 日程');
    const dtInputs = createPage.locator('aside[role="dialog"] input[type="datetime-local"]');
    await dtInputs.first().fill('2026-07-14T09:00');
    await dtInputs.nth(1).fill('2026-07-14T10:00');

    // Click create
    await createPage.locator('aside[role="dialog"] button[type="submit"]').click();

    await waitForWritebackPreview(createPage);
    assert.equal(captured.filter(c => c.url === 'writeback').length, 0,
      'No writeback request before create confirmation');

    // Confirm
    await writebackConfirmBtn(createPage).click();
    await waitForNoWritebackDialog(createPage);

    const wbCalls = captured.filter(c => c.url === 'writeback');
    assert.equal(wbCalls.length, 1, 'Exactly one writeback request for create');
    const payload = wbCalls[0].body as Record<string, unknown>;
    assert.equal(payload.operation, 'create');
    assert.ok(payload.calendarBindingId, 'create must include calendarBindingId');
    assert.equal(payload.scope, 'instance');
    assert.ok(!payload.eventId, 'create must not include eventId');
    assert.ok(payload.clientOperationId && typeof payload.clientOperationId === 'string');
    assert.ok((payload.clientOperationId as string).length > 0);
    if (payload.draft) {
      assert.equal((payload.draft as Record<string, unknown>).title, '新建 Outlook 日程');
    }

    await createPage.close();
    createPage = undefined;
  } finally {
    if (createPage) await createPage.close().catch(() => undefined);
    await context.close();
  }
}

// ─── Scenario E: Read-only and recurrence controls ────────────────────

async function runScenarioE(browser: Browser, baseUrl: string) {
  const [w, h] = viewports[2];
  const context = await newFixedContext(browser, {
    viewport: { width: w, height: h },
    timezoneId: DEFAULT_TIMEZONE_ID,
  });
  try {
    await context.addInitScript(() => {
      localStorage.setItem('accessToken', 'schedule-workbench-visual-audit-token');
    });
    await context.route('**/api/v1/**', route => {
      const url = new URL(route.request().url());
      const fullPath = url.pathname + url.search;
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(mockApiResponse(fullPath, route.request().method())),
      });
    });
    await context.route('**/api/v1/calendar/outlook/events/writeback', async route => {
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ code: 0, message: 'OK',
          data: { status: 'updated' }, timestamp: new Date().toISOString() }),
      });
    });

    const page = await context.newPage();
    await openCalendarMonth(page, baseUrl);

    // E1: Read-only calendar event — no save/delete, fields disabled
    await openEventByText(page, '只读日历 Outlook 事件');
    const hasSaveBtnR = await page.locator('aside[role="dialog"] button[type="submit"]').isVisible().catch(() => false);
    assert.ok(!hasSaveBtnR, 'Read-only editor must not show save button');
    const hasDeleteBtnR = await page.locator('aside[role="dialog"] button', { hasText: '删除' }).isVisible().catch(() => false);
    assert.ok(!hasDeleteBtnR, 'Read-only editor must not show delete button');
    const titleDisabled = await page.locator('aside[role="dialog"] input[type="text"]').first().isDisabled().catch(() => false);
    assert.ok(titleDisabled, 'Title input must be disabled in read-only');
    const calSelectDisabled = await page.locator('aside[role="dialog"] select').first().isDisabled().catch(() => false);
    assert.ok(calSelectDisabled, 'Calendar select must be disabled in read-only');
    await page
      .locator('aside[role="dialog"]')
      .getByText('此日历为只读，无法编辑或删除。', { exact: true })
      .waitFor({ state: 'visible', timeout: 3_000 });

    // Close and open single-instance Outlook event — no scope radio
    await page.locator('aside[role="dialog"] footer button', { hasText: '取消' }).click();
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 3_000 }).catch(() => undefined);

    await openEventByText(page, 'Outlook 单实例事件');
    const scopeRadioSingle = page.locator('input[name="outlook-scope"]');
    const hasScopeRadio = await scopeRadioSingle.isVisible({ timeout: 2_000 }).catch(() => false);
    assert.ok(!hasScopeRadio, 'Single instance Outlook event must not show scope radio');

    // Close and open recurrence occurrence — scope radio appears
    await page.locator('aside[role="dialog"] footer button', { hasText: '取消' }).click();
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 3_000 }).catch(() => undefined);

    await openEventByText(page, 'Outlook 可编辑事件');

    // Edit and save to enter writeback preview, where scope radio is rendered
    await page.locator('aside[role="dialog"] input[type="text"]').first().fill('范围测试');
    await page.locator('aside[role="dialog"] button[type="submit"]').click();
    await waitForWritebackPreview(page);

    const hasInstanceRadio = await page.locator('input[name="outlook-scope"][value="instance"]').isVisible({ timeout: 3_000 }).catch(() => false);
    const hasSeriesRadio = await page.locator('input[name="outlook-scope"][value="series"]').isVisible({ timeout: 2_000 }).catch(() => false);
    assert.ok(hasInstanceRadio && hasSeriesRadio, 'Recurring occurrence must show scope radio in writeback dialog');

    // Close writeback dialog
    await page.keyboard.press('Escape');
    await page.waitForFunction(
      () => !document.querySelector('div[role="dialog"][aria-modal="true"]'),
      null, { timeout: 3_000 },
    ).catch(() => undefined);

    // No recurrence pattern editing controls in the editor (read-only summary only)
    const recurrenceSection = page.locator(
      'aside[role="dialog"] section[data-event-editor-section]',
      { hasText: '重复' },
    );
    const hasRruleControl = await recurrenceSection
      .locator('input, textarea, select')
      .count()
      .catch(() => 0);
    assert.equal(hasRruleControl, 0, 'No recurrence pattern control rendered');

    // Close editor before new-event scenario
    await page.locator('aside[role="dialog"] footer button', { hasText: '取消' }).click();
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 3_000 }).catch(() => undefined);

    // E2: SeriesMaster — scope radio with series default
    await openEventByText(page, 'Outlook 系列主事件');
    await page.locator('aside[role="dialog"] input[type="text"]').first().fill('系列主事件已编辑');
    await page.locator('aside[role="dialog"] button[type="submit"]').click();
    await waitForWritebackPreview(page);

    const smInstanceRadio = page.locator('input[name="outlook-scope"][value="instance"]');
    const smSeriesRadio = page.locator('input[name="outlook-scope"][value="series"]');
    await smInstanceRadio.waitFor({ state: 'visible', timeout: 3_000 });
    await smSeriesRadio.waitFor({ state: 'visible', timeout: 3_000 });
    assert.ok(await smSeriesRadio.isChecked(), 'seriesMaster must default to series scope');

    // Close preview
    await page.keyboard.press('Escape');
    await page.waitForFunction(
      () => !document.querySelector('div[role="dialog"][aria-modal="true"]'),
      null, { timeout: 3_000 },
    ).catch(() => undefined);

    // Close editor
    await page.locator('aside[role="dialog"] footer button', { hasText: '取消' }).click();
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 3_000 }).catch(() => undefined);

    // E3: Exception event — scope radio with instance default
    await openEventByText(page, 'Outlook 例外事件');
    await page.locator('aside[role="dialog"] input[type="text"]').first().fill('例外事件已编辑');
    await page.locator('aside[role="dialog"] button[type="submit"]').click();
    await waitForWritebackPreview(page);

    const excInstanceRadio = page.locator('input[name="outlook-scope"][value="instance"]');
    const excSeriesRadio = page.locator('input[name="outlook-scope"][value="series"]');
    await excInstanceRadio.waitFor({ state: 'visible', timeout: 3_000 });
    await excSeriesRadio.waitFor({ state: 'visible', timeout: 3_000 });
    assert.ok(await excInstanceRadio.isChecked(), 'exception event must default to instance scope');

    // Close preview
    await page.keyboard.press('Escape');
    await page.waitForFunction(
      () => !document.querySelector('div[role="dialog"][aria-modal="true"]'),
      null, { timeout: 3_000 },
    ).catch(() => undefined);

    // Close editor
    await page.locator('aside[role="dialog"] footer button', { hasText: '取消' }).click();
    await page.waitForFunction(() => !document.querySelector('aside[role="dialog"]'),
      null, { timeout: 3_000 }).catch(() => undefined);

    // E4: New event with read-only calendar — calendar select stays enabled so user can switch
    const inboxPanel = page.getByRole('heading', { name: '收集箱', exact: true }).locator('../..');
    const addBtn = inboxPanel.getByRole('button', { name: '+ 新建', exact: true });
    await addBtn.waitFor({ state: 'visible', timeout: 5_000 });
    await addBtn.click();
    const scheduleOption = inboxPanel.getByRole('button', { name: '日程', exact: true });
    await scheduleOption.waitFor({ state: 'visible', timeout: 3_000 });
    await scheduleOption.click();
    await page
      .locator('aside[role="dialog"] h2', { hasText: '新建日程' })
      .waitFor({ state: 'visible', timeout: 5_000 });

    const calSelect = page.locator('aside[role="dialog"] select').first();
    await calSelect.waitFor({ state: 'visible', timeout: 3_000 });

    // Select read-only Outlook calendar
    await calSelect.selectOption({ label: 'Outlook 只读日历 (Outlook)' });

    const hasSubmitBtnRo = await page.locator('aside[role="dialog"] button[type="submit"]').isVisible().catch(() => false);
    assert.ok(!hasSubmitBtnRo, 'New event with read-only calendar must not show submit button');

    const titleDisabledRo = await page.locator('aside[role="dialog"] input[type="text"]').first().isDisabled().catch(() => false);
    assert.ok(titleDisabledRo, 'Title input must be disabled with read-only calendar');

    // Calendar select must remain enabled so user can switch away
    const calSelectEnabledRo = await calSelect.isEnabled().catch(() => false);
    assert.ok(calSelectEnabledRo, 'Calendar select must stay enabled with read-only calendar (new event)');

    await page
      .locator('aside[role="dialog"]')
      .getByText('此日历为只读，无法编辑或删除。', { exact: true })
      .waitFor({ state: 'visible', timeout: 3_000 });

    await page.locator('form#event-editor-form').evaluate(form => (form as HTMLFormElement).requestSubmit());
    const hasReadonlyPreview = await page
      .locator('div[role="dialog"][aria-modal="true"]')
      .isVisible({ timeout: 1_000 })
      .catch(() => false);
    assert.ok(!hasReadonlyPreview, 'Read-only new event must ignore direct form submission');

    // Switch back to a writable calendar — controls re-enable
    await calSelect.selectOption({ label: 'Outlook 工作日历 (Outlook)' });
    const titleReEnabled = await page.locator('aside[role="dialog"] input[type="text"]').first().isEnabled().catch(() => false);
    assert.ok(titleReEnabled, 'Title input must be re-enabled after switching to writable calendar');
    const hasSubmitAfterSwitch = await page.locator('aside[role="dialog"] button[type="submit"]').isVisible().catch(() => false);
    assert.ok(hasSubmitAfterSwitch, 'Submit button must reappear after switching to writable calendar');

    await page.close();
  } finally {
    await context.close();
  }
}
