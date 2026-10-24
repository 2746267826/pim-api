import type { S2ThreeStateDistribution } from '../../api/dataReliabilityTypes';
import { buildThreeStateBuckets, formatDurationSeconds } from './dataReliabilityModel';

/**
 * S2 专属：三态分布（操作活跃 / 观看活跃 / 疑似未收尾）。
 * 目的：让用户一眼看出"有多少时长其实是没人收尾的挂机时间"（#261 §2）。
 */
export default function S2ThreeStateDistribution({ distribution }: { distribution: S2ThreeStateDistribution }) {
  const buckets = buildThreeStateBuckets(distribution);

  return (
    <div className="space-y-2" data-testid="s2-three-state">
      <div className="flex h-3 w-full overflow-hidden rounded-full bg-slate-100" role="img" aria-label="S2 三态时长占比">
        {buckets.map(bucket => (
          <span
            key={bucket.key}
            className={bucket.className}
            style={{ width: `${Math.max(0, Math.min(100, bucket.percent))}%` }}
          />
        ))}
      </div>
      <ul className="grid gap-1 text-xs text-slate-600 sm:grid-cols-3">
        {buckets.map(bucket => (
          <li key={bucket.key} className="flex items-center gap-1">
            <span className={`inline-block h-2 w-2 shrink-0 rounded-full ${bucket.className}`} aria-hidden="true" />
            <span className="text-slate-700">{bucket.label}</span>
            <span>
              {bucket.percent.toFixed(1)}% · {formatDurationSeconds(bucket.seconds)} · {bucket.count} 个
            </span>
          </li>
        ))}
      </ul>
      <p className="text-xs text-slate-500">
        三态合计 {formatDurationSeconds(distribution.totalSeconds)}
        {distribution.declaredGapSeconds > 0 && (
          <>；另有明确空档 {formatDurationSeconds(distribution.declaredGapSeconds)}（本身声明"这里没有人"，不计入活跃时长）</>
        )}
      </p>
    </div>
  );
}
