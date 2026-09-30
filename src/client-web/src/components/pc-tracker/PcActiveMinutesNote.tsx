import { PC_ACTIVE_MINUTES_NOTE } from '../charts/pcTodayOptions';

/**
 * REQ-8：「PC 活跃」口径说明（卡片页脚小字）。
 *
 * 放在 PC 记录概览卡片页脚，与「活跃」数值同屏 —— 用户看到数值变大时能立刻读到原因。
 * 文案来自 `PC_ACTIVE_MINUTES_NOTE`（单一来源，测试断言同一常量）。
 */
export default function PcActiveMinutesNote({ className = '' }: { className?: string }) {
  return (
    <p
      data-pc-active-minutes-note
      className={`text-[10px] leading-4 text-zinc-400 ${className}`.trim()}
      title={PC_ACTIVE_MINUTES_NOTE}
    >
      {PC_ACTIVE_MINUTES_NOTE}
    </p>
  );
}
