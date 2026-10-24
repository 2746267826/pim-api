import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import ClassificationActionQueue from '../ClassificationActionQueue';
import ContextConfirmationPanel from '../ContextConfirmationPanel';
import type { ActivityClassificationSuggestion } from '../../../types';

/**
 * WO-FRONTEND-PC-CONTRACT-20260930 · REQ-5
 * AC-5.1 建议卡片上显示的日期来源为 `generatedForDate`（业务日）。
 * AC-5.2 后端返回含历史业务日的建议时，界面能正确显示各自日期。
 */

function suggestion(over: Partial<ActivityClassificationSuggestion> = {}): ActivityClassificationSuggestion {
  return {
    id: 's1',
    clusterKey: 'app:java',
    generatedForDate: '2026-09-04',
    sampleCount: 12,
    totalDurationSeconds: 600,
    sampleRecordsJson: '[]',
    sanitizedContextJson: '{}',
    currentCategory: null,
    suggestedCategory: '编程/折腾',
    suggestedProjectTag: null,
    suggestedRulesJson: null,
    userFeedback: null,
    llmResponseJson: null,
    status: 'pending',
    appDisplayName: 'java',
    appIcon: null,
    recognitionSource: null,
    ...over,
  };
}

describe('REQ-5 · 分类建议按 generatedForDate 归日', () => {
  it('ClassificationActionQueue 显示 generatedForDate（不是请求日期）', () => {
    const { container } = render(
      <ClassificationActionQueue
        suggestions={[suggestion()]}
        isLoading={false}
        onPreview={() => {}}
        onReject={() => {}}
      />,
    );
    const text = container.textContent ?? '';
    expect(text).toContain('2026-09-04');
  });

  it('ContextConfirmationPanel 显示 generatedForDate', () => {
    const { container } = render(
      <ContextConfirmationPanel
        suggestions={[suggestion()]}
        isLoading={false}
        onPreview={() => {}}
        onReject={() => {}}
      />,
    );
    expect(container.textContent ?? '').toContain('2026-09-04');
  });

  it('AC-5.2 · 多条建议各自显示自己的业务日', () => {
    const items = [
      suggestion({ id: 'a', clusterKey: 'app:java', generatedForDate: '2026-09-04', appDisplayName: 'java' }),
      suggestion({ id: 'b', clusterKey: 'app:python', generatedForDate: '2026-09-11', appDisplayName: 'python' }),
      suggestion({ id: 'c', clusterKey: 'app:code', generatedForDate: '2026-09-27', appDisplayName: 'code' }),
    ];
    const { container } = render(
      <ClassificationActionQueue
        suggestions={items}
        isLoading={false}
        onPreview={() => {}}
        onReject={() => {}}
      />,
    );
    const text = container.textContent ?? '';
    for (const item of items) {
      expect(text, `应显示 ${item.generatedForDate}`).toContain(item.generatedForDate!);
    }
  });

  it('缺少 generatedForDate 时不显示伪造日期（兜底不崩）', () => {
    const { container } = render(
      <ClassificationActionQueue
        suggestions={[suggestion({ generatedForDate: undefined as unknown as string })]}
        isLoading={false}
        onPreview={() => {}}
        onReject={() => {}}
      />,
    );
    expect(container.textContent ?? '').not.toContain('undefined');
    expect(container.textContent ?? '').not.toContain('Invalid Date');
  });
});
