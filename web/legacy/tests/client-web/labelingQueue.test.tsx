import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import type { LabelingQueueItem } from '../../src/client-web/src/api/classificationLabeling';
import {
  buildLabelRequest,
  mergeCustomCategories,
} from '../../src/client-web/src/components/labeling/labelingUtils';
import LabelingQueue, {
  type LabelingQueueData,
} from '../../src/client-web/src/components/labeling/LabelingQueue';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
(globalThis as typeof globalThis & { React: typeof React }).React = React;

function test(_name: string, run: () => void) {
  run();
}

const dictionary = [
  { id: '1', name: '编程/折腾', color: '#6B5EE4', icon: '💻' },
  { id: '2', name: '学习', color: '#14b8a6', icon: '📚' },
  { id: '3', name: '其他', color: '#64748b', icon: '📋' },
];

const domainItem: LabelingQueueItem = {
  targetType: 'domain',
  target: 'dev.to',
  displayName: 'DEV 社区',
  minutes: 15,
  sampleTitles: ['dev.to daily digest'],
};

const appItem: LabelingQueueItem = {
  targetType: 'app',
  target: 'mobaxterm',
  displayName: 'MobaXterm',
  minutes: 42,
  sampleTitles: ['ssh to 192.168.1.1'],
};

const queueData: LabelingQueueData = {
  items: [domainItem, appItem],
  dictionary,
};

test('mergeCustomCategories 合并去重且自定义分类追加在预置分类之后', () => {
  assert.deepEqual(
    mergeCustomCategories(['编程/折腾', '学习'], ['学习', '其他', '学习']),
    ['编程/折腾', '学习', '其他'],
  );
  assert.deepEqual(
    mergeCustomCategories(['编程/折腾'], ['写日记', '学习']),
    ['编程/折腾', '写日记', '学习'],
  );
  assert.deepEqual(mergeCustomCategories([], []), []);
  assert.deepEqual(mergeCustomCategories([], ['a', 'b']), ['a', 'b']);
});

test('buildLabelRequest scope=all 不带 keyword，scope=keyword 携带 keyword', () => {
  assert.deepEqual(buildLabelRequest(domainItem, '编程/折腾', 'all'), {
    targetType: 'domain',
    target: 'dev.to',
    categoryName: '编程/折腾',
    scope: 'all',
  });
  assert.equal(
    Object.prototype.hasOwnProperty.call(buildLabelRequest(domainItem, '编程/折腾', 'all'), 'keyword'),
    false,
  );
  assert.deepEqual(buildLabelRequest(appItem, '学习', 'keyword', '教程'), {
    targetType: 'app',
    target: 'mobaxterm',
    categoryName: '学习',
    scope: 'keyword',
    keyword: '教程',
  });
});

test('LabelingQueue 静态渲染展示队列项、预置分类与自定义输入', () => {
  const html = renderToStaticMarkup(
    React.createElement(LabelingQueue, {
      fetchData: async () => queueData,
      initialItems: queueData.items,
      initialDictionary: queueData.dictionary,
      initialLoading: false,
    }),
  );

  for (const text of [
    'DEV 社区',
    'MobaXterm',
    '编程/折腾',
    '学习',
    '其他',
    '自定义分类，回车添加…',
    '所有情况',
    '仅含关键词页面',
  ]) {
    assert.equal(html.includes(text), true, `LabelingQueue 静态渲染应包含: ${text}`);
  }
});

test('LabelingQueue 空数据时渲染暂无待分类项', () => {
  const html = renderToStaticMarkup(
    React.createElement(LabelingQueue, {
      fetchData: async () => ({ items: [], dictionary: [] }),
      initialItems: [],
      initialDictionary: [],
      initialLoading: false,
    }),
  );

  assert.equal(html.includes('暂无待分类项'), true);
});
