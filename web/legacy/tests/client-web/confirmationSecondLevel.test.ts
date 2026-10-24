import assert from 'node:assert/strict';
import { describe, it, before } from 'node:test';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

const requireFromWeb = createRequire(new URL('../../src/client-web/package.json', import.meta.url));
const { JSDOM } = requireFromWeb('jsdom') as typeof import('jsdom');

const React = requireFromWeb('react') as typeof import('react');
const { act } = React;
const { createRoot } = requireFromWeb('react-dom/client') as typeof import('react-dom/client');

let QueryClient: typeof import('@tanstack/react-query').QueryClient;
let QueryClientProvider: typeof import('@tanstack/react-query').QueryClientProvider;

let getConfirmActionState: typeof import('../../src/client-web/src/pages/ConfirmationsPage').getConfirmActionState;
let ConfirmationsPage: typeof import('../../src/client-web/src/pages/ConfirmationsPage').default;
let container: HTMLElement;
let root: ReturnType<typeof createRoot>;

before(async () => {
  // ConfirmationsPage transitively imports safeHtml/DOMPurify, which requires a
  // DOM at module load. Install jsdom globals before the dynamic import so the
  // dependency chain initializes cleanly without touching production code.
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

  // react-query ships separate ESM and CJS builds; the app imports the ESM
  // build, so load the same build here to share the QueryClient context.
  const reactQueryEsmUrl = requireFromWeb.resolve('@tanstack/react-query').replace(/\.cjs$/, '.js');
  const reactQuery = (await import(pathToFileURL(reactQueryEsmUrl).href)) as typeof import('@tanstack/react-query');
  QueryClient = reactQuery.QueryClient;
  QueryClientProvider = reactQuery.QueryClientProvider;

  const mod = await import('../../src/client-web/src/pages/ConfirmationsPage');
  getConfirmActionState = mod.getConfirmActionState;
  ConfirmationsPage = mod.default;
  container = dom.window.document.createElement('div');
  dom.window.document.body.appendChild(container);
  root = createRoot(container);
});

describe('getConfirmActionState second-level confirmation', () => {
  it('keeps plain Confirm when no second level is required', () => {
    assert.deepEqual(
      getConfirmActionState(false, false),
      { label: 'Confirm', requiresArm: false },
    );
  });

  it('arms a second-level Confirm when required and not yet armed', () => {
    assert.deepEqual(
      getConfirmActionState(true, false),
      { label: 'Confirm', requiresArm: true },
    );
  });

  it('confirms final action once the second level is armed', () => {
    assert.deepEqual(
      getConfirmActionState(true, true),
      { label: 'Confirm final', requiresArm: false },
    );
  });
});

describe('ConfirmationsPage 外部回写影响 rendering', () => {
  it('hides a raw GraphEventId value behind the static Chinese safe summary', async () => {
    const confirmation = {
      id: 'conf-1',
      operationType: 'outlookWriteback',
      summary: '写回 Microsoft Outlook 事件',
      riskLevel: 'L2SingleWriteback',
      source: 'outlook',
      status: 'Pending',
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      createdAt: new Date().toISOString(),
      payloadJson: '{}',
      previewJson: '',
      requiresSecondLevelConfirmation: true,
      changedFields: ['title', 'dtStart'],
      externalEffect: 'GraphEventId=AAMkADe1f2g3h4i5j6k7l8m9n0p1q2',
      beforeJson: null,
      afterJson: null,
    };

    const originalFetch = globalThis.fetch;
    globalThis.fetch = async (input: RequestInfo | URL) => {
      const url = String(input);
      const data = url.includes('/operations/confirmations/pending') ? [confirmation] : confirmation;
      return new Response(JSON.stringify({ code: 0, message: 'OK', data }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    };

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    try {
      await act(async () => {
        root.render(
          React.createElement(
            QueryClientProvider,
            { client: queryClient },
            React.createElement(ConfirmationsPage),
          ),
        );
      });
      for (let i = 0; i < 5; i++) {
        await act(async () => {
          await new Promise(resolve => setTimeout(resolve, 0));
        });
      }
      const text = container.textContent ?? '';
      assert.ok(text.includes('外部回写影响'), 'detail panel must render the 外部回写影响 row');
      assert.ok(text.includes('（外部标识已隐藏）'), 'Graph event id must collapse to the static safe summary');
      assert.ok(!text.includes('GraphEventId'), 'raw GraphEventId text must never render');
      assert.ok(!text.includes('AAMkAD'), 'raw Graph event id must never render');
    } finally {
      await act(async () => {
        root.unmount();
      });
      container.textContent = '';
      queryClient.clear();
      globalThis.fetch = originalFetch;
    }
  });
});
