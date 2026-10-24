import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  getActivityClassificationRules,
  getActivityClassificationSettings,
  saveActivityClassificationSettings,
} from '../api/pcTracker';
import ClassificationRecomputePanel from '../components/pc-classification/ClassificationRecomputePanel';
import ClassificationRuleEditor from '../components/pc-classification/ClassificationRuleEditor';
import ClassificationRuleTable from '../components/pc-classification/ClassificationRuleTable';
import PageHeader from '../ui/PageHeader';
import type { ActivityClassificationRule } from '../types';

export default function PcClassificationPage() {
  const queryClient = useQueryClient();
  const [selectedRuleId, setSelectedRuleId] = useState<string | null>(null);
  const [selectedMinutes, setSelectedMinutes] = useState(5);

  const {
    data: rules = [],
    isLoading: rulesLoading,
  } = useQuery({
    queryKey: ['pc-classification-rules'],
    queryFn: getActivityClassificationRules,
  });

  const { data: settings } = useQuery({
    queryKey: ['pc-classification-settings'],
    queryFn: getActivityClassificationSettings,
  });

  const selectedRule = useMemo<ActivityClassificationRule | null>(() => {
    if (!selectedRuleId) return null;
    return rules.find(rule => rule.id === selectedRuleId) ?? null;
  }, [rules, selectedRuleId]);

  const saveSettingsMut = useMutation({
    mutationFn: saveActivityClassificationSettings,
    onSuccess: data => {
      setSelectedMinutes(data.recommendedMinimumClassificationDurationMinutes);
      queryClient.invalidateQueries({ queryKey: ['pc-classification-settings'] });
      queryClient.invalidateQueries({ queryKey: ['pc-summary'] });
      queryClient.invalidateQueries({ queryKey: ['pc-classification-suggestions'] });
    },
  });

  useEffect(() => {
    if (settings) {
      setSelectedMinutes(settings.recommendedMinimumClassificationDurationMinutes);
    }
  }, [settings]);

  const savedMinutes = settings?.recommendedMinimumClassificationDurationMinutes ?? 5;
  const isDirty = selectedMinutes !== savedMinutes;

  return (
    <div className="mx-auto w-full max-w-[1500px] space-y-4 pb-20">
      <PageHeader
        title="分类管理"
        subtitle="管理 PC 活动分类规则和显示粒度"
      />

      <ClassificationRecomputePanel
        settings={settings}
        selectedMinutes={selectedMinutes}
        onSelectedMinutesChange={setSelectedMinutes}
        onSaveSettings={() => saveSettingsMut.mutate(selectedMinutes)}
        isSaving={saveSettingsMut.isPending}
        isDirty={isDirty}
      />

      {saveSettingsMut.isError && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          保存设置失败，请稍后重试。
        </div>
      )}

      <div className="grid grid-cols-1 gap-4">
        <div className="min-w-0 overflow-auto pb-20">
          <ClassificationRuleTable
            rules={rules}
            selectedRuleId={selectedRuleId}
            isLoading={rulesLoading}
            onEdit={rule => setSelectedRuleId(rule.id)}
          />
        </div>
        {/* Desktop inline editor */}
        <div className="hidden xl:block">
          <ClassificationRuleEditor
            rule={selectedRule}
            onClose={() => setSelectedRuleId(null)}
          />
        </div>
      </div>

      {/* Mobile drawer for rule editor: fixed inset-0 on small screens */}
      {selectedRule && (
        <div className="fixed inset-0 z-40 flex justify-end xl:hidden">
          <div className="absolute inset-0 bg-slate-950/30" onClick={() => setSelectedRuleId(null)} />
          <div className="relative flex h-full w-full max-w-[420px] flex-col overflow-auto bg-white shadow-xl">
            <ClassificationRuleEditor
              rule={selectedRule}
              onClose={() => setSelectedRuleId(null)}
            />
          </div>
        </div>
      )}
    </div>
  );
}
