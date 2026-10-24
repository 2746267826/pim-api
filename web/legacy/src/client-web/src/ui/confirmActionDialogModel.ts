import type { CalendarOperationSample } from '../types';

export interface DeleteConfirmationInput {
  targetType: string;
  title: string;
  affectedCount: number;
  samples: CalendarOperationSample[];
}

export interface DeleteConfirmationCopy {
  title: string;
  description: string;
  confirmLabel: string;
  samples: CalendarOperationSample[];
}

export function getDeleteTargetTypeLabel(targetType: string) {
  if (targetType === 'calendar' || targetType === 'calendar-book') return '日历本';
  if (targetType === 'task-book') return '任务本';
  if (targetType === 'task') return '任务';
  return '日程';
}

export function getOperationSampleTypeLabel(type: string) {
  if (type === 'calendar' || type === 'calendar-book') return '日历本';
  if (type === 'task-book') return '任务本';
  if (type === 'task') return '任务';
  return '日程';
}

export function buildDeleteConfirmationCopy(input: DeleteConfirmationInput): DeleteConfirmationCopy {
  const typeLabel = getDeleteTargetTypeLabel(input.targetType);

  if (input.affectedCount <= 1) {
    return {
      title: `删除${typeLabel}`,
      description: `${input.title} 将移动到回收站，可以在设置中恢复。`,
      confirmLabel: '移动到回收站',
      samples: input.samples,
    };
  }

  return {
    title: `删除${typeLabel}`,
    description: `${input.title} 和 ${input.affectedCount} 个关联项目将一起移动到回收站。`,
    confirmLabel: `确认移动 ${input.affectedCount} 项`,
    samples: input.samples,
  };
}
