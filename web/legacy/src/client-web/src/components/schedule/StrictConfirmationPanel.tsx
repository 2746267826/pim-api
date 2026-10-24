import type { OperationConfirmation } from '../../types';

interface StrictConfirmationPanelProps {
  confirmation: OperationConfirmation;
  armed: boolean;
  onArm: () => void;
}

export default function StrictConfirmationPanel({
  confirmation,
  armed,
  onArm,
}: StrictConfirmationPanelProps) {
  const strict = confirmation.requiresStrictConfirmation || confirmation.riskLevel === 'L4BatchOrDestructiveGovernance';

  return (
    <section className={`rounded-lg border p-3 text-sm ${
      strict ? 'border-red-200 bg-red-50 text-red-800' : 'border-amber-200 bg-amber-50 text-amber-800'
    }`}>
      <h3 className="font-semibold">{strict ? '严格确认' : '二级确认'}</h3>
      <p className="mt-1 text-xs leading-5">
        {strict
          ? 'L4 或破坏性治理操作需要严格确认，并保留恢复路径。'
          : '此操作需要二级确认，先复核影响对象、来源和回写影响。'}
      </p>
      <button
        type="button"
        onClick={onArm}
        disabled={armed}
        className="mt-3 rounded-lg border border-current px-3 py-1.5 text-xs font-semibold disabled:opacity-60"
      >
        {armed ? '已就绪' : '我已复核'}
      </button>
    </section>
  );
}
