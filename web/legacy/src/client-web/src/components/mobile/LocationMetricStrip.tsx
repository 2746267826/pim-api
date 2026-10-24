import type { MobileLocationAnalyticsOverview } from '../../api/mobile';
import MobileMetricGrid from './MobileMetricGrid';
import {
  formatAccuracyLabel,
  formatDistanceMeters,
  formatDurationSeconds,
  qualityFlagLabel,
} from './locationFormatting';

export default function LocationMetricStrip({
  overview,
}: {
  overview: MobileLocationAnalyticsOverview | null | undefined;
}) {
  const qualityHelper = overview?.qualityFlags.length
    ? overview.qualityFlags.map(qualityFlagLabel).join('、')
    : '质量正常';

  return (
    <MobileMetricGrid
      items={[
        {
          label: '定位点',
          value: String(overview?.pointCount ?? 0),
          helper: `保留 ${overview?.usablePointCount ?? 0} 个，拒绝 ${overview?.rejectedPointCount ?? 0} 个`,
        },
        {
          label: '活跃跨度',
          value: formatDurationSeconds(overview?.activeSpanSeconds),
          helper: '按首尾可用定位点估算',
        },
        {
          label: '估算里程',
          value: formatDistanceMeters(overview?.distanceMeters),
          helper: '按轨迹片段累计',
        },
        {
          label: '停留点',
          value: String(overview?.stayCount ?? 0),
          helper: `最长 ${formatDurationSeconds(overview?.longestStaySeconds)}`,
        },
        {
          label: '平均误差',
          value: formatAccuracyLabel(overview?.averageAccuracyMeters),
          helper: 'GPS / 网络混合',
        },
        {
          label: '质量提示',
          value: `${overview?.qualityIssueCount ?? 0} 条`,
          helper: qualityHelper,
          tone: (overview?.qualityIssueCount ?? 0) > 0 ? 'warning' : 'good',
        },
      ]}
    />
  );
}
