import type { CalendarOperationResult, ImportReport } from '../types';

interface OperationResultBannerProps {
  result: CalendarOperationResult | ImportReport | null;
  onDismiss: () => void;
}

function isCalendarOperationResult(result: CalendarOperationResult | ImportReport): result is CalendarOperationResult {
  return 'message' in result && 'affectedCount' in result;
}

export function OperationResultBanner({ result, onDismiss }: OperationResultBannerProps) {
  if (!result) return null;

  if (isCalendarOperationResult(result)) {
    return (
      <div
        role="status"
        className="rounded-lg border border-teal-200 bg-teal-50 px-4 py-3 text-sm text-teal-900"
      >
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="font-medium">{result.message}</p>
            <p className="mt-1 text-teal-700">影响 {result.affectedCount} 项。</p>
          </div>
          <button type="button" onClick={onDismiss} className="rounded-md px-2 py-1 text-xs font-medium text-teal-700 hover:bg-teal-100">
            关闭
          </button>
        </div>
      </div>
    );
  }

  return (
    <div
      role="status"
      className="rounded-lg border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-950"
    >
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="font-medium">导入完成</p>
          <p className="mt-1 text-blue-800">
            已导入 {result.imported} 项，跳过 {result.skipped} 项。
          </p>
          {Object.keys(result.skippedReasons).length > 0 && (
            <ul className="mt-2 space-y-1 text-xs text-blue-800">
              {Object.entries(result.skippedReasons).map(([reason, count]) => (
                <li key={reason}>
                  {reason}: {count} 项
                </li>
              ))}
            </ul>
          )}
        </div>
        <button type="button" onClick={onDismiss} className="rounded-md px-2 py-1 text-xs font-medium text-blue-700 hover:bg-blue-100">
          关闭
        </button>
      </div>
    </div>
  );
}

export default OperationResultBanner;
