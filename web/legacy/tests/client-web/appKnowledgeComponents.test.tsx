import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import type { AppKnowledgeContextPattern } from '../../src/client-web/src/api/appKnowledge';
import AppKnowledgeContextList from '../../src/client-web/src/components/app-knowledge/AppKnowledgeContextList';
import AppKnowledgeTabs from '../../src/client-web/src/components/app-knowledge/AppKnowledgeTabs';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const clientSourceRoot = path.join(process.cwd(), 'src/client-web/src');
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const { MemoryRouter } = requireFromClient('react-router-dom') as typeof import('react-router-dom');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

function test(_name: string, run: () => void) {
  run();
}

function readClientSource(relativePath: string) {
  return readFileSync(path.join(clientSourceRoot, relativePath), 'utf8');
}

test('category tree secondary navigation uses app knowledge language', () => {
  const html = renderToStaticMarkup(
    React.createElement(
      MemoryRouter,
      null,
      React.createElement(AppKnowledgeTabs, { active: 'categories' })
    )
  );

  assert.equal(html.includes('App 列表'), true);
  assert.equal(html.includes('分类树'), true);
  assert.equal(html.includes('分类管理'), false);
});

test('category tree page uses app knowledge category tree language and tabs', () => {
  const source = readClientSource('pages/CategoryTreePage.tsx');

  assert.equal(source.includes('PageHeader title="分类树"'), true);
  assert.equal(source.includes('subtitle="作为 App 知识库的目标分类结构"'), true);
  assert.equal(source.includes('<AppKnowledgeTabs active="categories" />'), true);
  assert.equal(source.includes('分类管理'), false);
  assert.equal(source.includes('Classification Management'), false);
});

test('app knowledge base page keeps app tab active with updated subtitle', () => {
  const source = readClientSource('pages/AppKnowledgeBasePage.tsx');

  assert.equal(source.includes('title="App 知识库"'), true);
  assert.equal(source.includes('subtitle="管理应用、域名、标题模式和分类归属知识"'), true);
  assert.equal(source.includes('<AppKnowledgeTabs active="apps" />'), true);
  assert.equal(source.includes('getAppKnowledgeApps'), true);
  assert.equal(source.includes('getAppKnowledgeContexts'), true);
  assert.equal(source.includes('deleteAppKnowledgeContext'), true);
  assert.equal(source.includes('<AppKnowledgeContextList'), true);
});

test('context list renders context knowledge pattern details', () => {
  const context: AppKnowledgeContextPattern = {
    id: 'context-1',
    appId: 'app-1',
    processName: 'chrome.exe',
    patternType: 'domain',
    patternValue: 'github.com',
    targetCategoryName: 'Development',
    projectTag: 'pim-platform',
    scopeSummary: 'github.com work',
    source: 'learned',
    confidence: 0.91,
    enabled: true,
    affectedRecordCount: 12,
    affectedDurationSeconds: 45 * 60,
    lastMatchedAt: '2026-07-06T08:00:00Z',
  };

  const html = renderToStaticMarkup(
    React.createElement(AppKnowledgeContextList, {
      contexts: [context],
      isLoading: false,
      onDelete: () => undefined,
    })
  );

  assert.equal(html.includes('上下文知识'), true);
  assert.equal(html.includes('域名'), true);
  assert.equal(html.includes('学习'), true);
  assert.equal(html.includes('影响 12 条记录'), true);
  assert.equal(html.includes('影响时长 45 分钟'), true);
  assert.equal(html.includes('github.com'), true);
  assert.equal(html.includes('Development'), true);
  assert.equal(html.includes('pim-platform'), true);
});

test('app knowledge UI copy does not expose English remnants', () => {
  const source = [
    readClientSource('pages/AppKnowledgeBasePage.tsx'),
    readClientSource('pages/PcTrackerPage.tsx'),
    readClientSource('components/app-knowledge/AppKnowledgeContextList.tsx'),
    readClientSource('components/app-knowledge/AppKnowledgeImpactSummary.tsx'),
  ].join('\n');

  const forbiddenEnglishUiCopy = [
    'Selected for context',
    'Context patterns',
    'Select an app',
    'recent impact',
    'Choose a row to inspect context knowledge patterns.',
    'context patterns',
    'pending contexts',
    'Delete this context knowledge pattern?',
    'Select an app row to view context knowledge.',
    'No target assigned',
    'Loading context knowledge...',
    'No context knowledge patterns yet.',
    'Context knowledge',
    'Disabled',
    'App default',
    'Domain',
    'Window title',
    'URL path',
    'Source family',
    'affected records',
    'affected minutes',
    'drilldown',
  ];

  for (const text of forbiddenEnglishUiCopy) {
    assert.equal(source.includes(text), false, `should translate UI copy: ${text}`);
  }
});
