interface Props {
  affectedRecordCount: number;
  affectedDurationSeconds: number;
  pendingContextCount?: number;
}

function formatMinutes(seconds: number) {
  const minutes = Math.round(seconds / 60);
  return minutes.toLocaleString();
}

export default function AppKnowledgeImpactSummary({
  affectedRecordCount,
  affectedDurationSeconds,
  pendingContextCount,
}: Props) {
  return (
    <div className="flex flex-wrap gap-2 text-xs text-slate-600">
      <span className="rounded border border-slate-200 bg-white px-2 py-1">
        影响 {affectedRecordCount.toLocaleString()} 条记录
      </span>
      <span className="rounded border border-slate-200 bg-white px-2 py-1">
        影响时长 {formatMinutes(affectedDurationSeconds)} 分钟
      </span>
      {typeof pendingContextCount === 'number' && (
        <span className="rounded border border-amber-200 bg-amber-50 px-2 py-1 text-amber-700">
          待确认上下文 {pendingContextCount.toLocaleString()} 项
        </span>
      )}
    </div>
  );
}
