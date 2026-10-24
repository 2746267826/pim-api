import assert from 'node:assert/strict';
import { describe, it, before } from 'node:test';
import { createRequire } from 'node:module';
import type { ComponentType } from 'react';
import type { EventFieldDiffEntry } from '../../src/client-web/src/utils/eventFieldDiff';

const requireFromWeb = createRequire(new URL('../../src/client-web/package.json', import.meta.url));
const { JSDOM } = requireFromWeb('jsdom') as typeof import('jsdom');

const React = requireFromWeb('react') as typeof import('react');
const { act } = React;
const { createRoot } = requireFromWeb('react-dom/client') as typeof import('react-dom/client');

interface BeforeAfterDiffTestProps {
  before?: Record<string, unknown> | null;
  after?: Record<string, unknown> | null;
  diffs?: EventFieldDiffEntry[] | null;
  meta?: {
    operation?: string | null;
    accountName?: string | null;
    calendarName?: string | null;
    scope?: string | null;
  } | null;
  beforeJson?: string | null;
  afterJson?: string | null;
  changedFields?: string[] | null;
}

let BeforeAfterDiff: ComponentType<BeforeAfterDiffTestProps>;
let container: HTMLElement;
let root: ReturnType<typeof createRoot>;

before(async () => {
  const dom = new JSDOM('<!DOCTYPE html><html><body></body></html>');
  globalThis.window = dom.window as unknown as Window & typeof globalThis;
  globalThis.document = dom.window.document;
  globalThis.Node = dom.window.Node;
  globalThis.DocumentFragment = dom.window.DocumentFragment;
  globalThis.Element = dom.window.Element;
  globalThis.HTMLElement = dom.window.HTMLElement;
  globalThis.HTMLDocument = dom.window.HTMLDocument;
  globalThis.DOMParser = dom.window.DOMParser;
  globalThis.React = React;

  // React 19 requires this flag for act(...) to work outside react-test-renderer.
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

  const mod = await import('../../src/client-web/src/components/schedule/BeforeAfterDiff');
  BeforeAfterDiff = mod.default;
  container = dom.window.document.createElement('div');
  dom.window.document.body.appendChild(container);
  root = createRoot(container);
});

async function render(props: BeforeAfterDiffTestProps) {
  await act(async () => {
    root.render(React.createElement(BeforeAfterDiff, props));
  });
}

describe('BeforeAfterDiff legacy snapshot rendering', () => {
  it('shows an explicit Chinese no-snapshot state instead of fabricated rows', async () => {
    await render({ beforeJson: null, afterJson: null, changedFields: ['title', 'dtStart'] });
    const text = container.textContent ?? '';
    assert.ok(text.includes('没有结构化快照'),
      'Both snapshots unavailable must show the explicit no-snapshot state');
    assert.equal(container.querySelectorAll('li').length, 0,
      'Must not fabricate rows from changedFields when no snapshot exists');
    assert.ok(!text.includes('→'), 'No fabricated arrow rows may render');
    assert.ok(!text.includes('-'), 'No fabricated placeholder values may render');
  });

  it('renders real start/end values with canonical Chinese labels from legacy snapshots', async () => {
    await render({
      beforeJson: JSON.stringify({
        Title: '旧标题',
        Start: '2026-07-14T09:00:00',
        End: '2026-07-14T10:00:00',
      }),
      afterJson: JSON.stringify({
        Title: '新标题',
        DtStart: '2026-07-14T09:30:00',
        DtEnd: '2026-07-14T10:30:00',
      }),
    });
    const text = container.textContent ?? '';
    assert.ok(text.includes('标题'), 'Title row must use the canonical Chinese label');
    assert.ok(text.includes('开始时间'), 'Start row must use the canonical Chinese label');
    assert.ok(text.includes('结束时间'), 'End row must use the canonical Chinese label');
    assert.ok(text.includes('2026-07-14T09:00:00'), 'Before start value must render literally');
    assert.ok(text.includes('2026-07-14T09:30:00'), 'After start value must render literally');
    assert.ok(text.includes('2026-07-14T10:00:00'), 'Before end value must render literally');
    assert.ok(text.includes('2026-07-14T10:30:00'), 'After end value must render literally');
    assert.ok(!text.includes('DtStart') && !text.includes('DtEnd'),
      'Pascal-cased aliases must never surface as raw labels');
  });

  it('merges PascalCase before and camelCase after snapshots into one Chinese-labeled row', async () => {
    await render({
      beforeJson: JSON.stringify({ Subject: '旧主题', Location: '会议室 A' }),
      afterJson: JSON.stringify({ subject: '新主题', location: '会议室 A' }),
    });
    assert.equal(container.querySelectorAll('li').length, 1,
      'Pascal/camel variants of the same business key must merge into one row');
    const rowText = container.querySelector('li')?.textContent ?? '';
    assert.ok(rowText.includes('标题'), 'Merged row must use the canonical Chinese label');
    assert.ok(rowText.includes('旧主题'), 'Merged row must render the before value');
    assert.ok(rowText.includes('新主题'), 'Merged row must render the after value');
  });
});
