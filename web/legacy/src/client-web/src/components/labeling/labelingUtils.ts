import type { LabelingQueueItem, SubmitLabelRequest } from '../../api/classificationLabeling';

/**
 * 合并预置分类与自定义分类：以 base 顺序为先，追加 custom 中未出现过的新分类，整体去重。
 */
export function mergeCustomCategories(base: string[], custom: string[]): string[] {
  const seen = new Set<string>();
  const merged: string[] = [];
  for (const name of [...base, ...custom]) {
    if (seen.has(name)) continue;
    seen.add(name);
    merged.push(name);
  }
  return merged;
}

/**
 * 构造 POST /pc/classification/label 的请求体。
 * scope 为 'keyword' 时携带 keyword，'all' 时不携带。
 */
export function buildLabelRequest(
  item: Pick<LabelingQueueItem, 'targetType' | 'target'>,
  categoryName: string,
  scope: 'all' | 'keyword',
  keyword?: string,
): SubmitLabelRequest {
  const body: SubmitLabelRequest = {
    targetType: item.targetType,
    target: item.target,
    categoryName,
    scope,
  };
  if (scope === 'keyword') {
    body.keyword = keyword ?? '';
  }
  return body;
}
