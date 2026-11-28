/**
 * 手动重试的真实回执文案。
 *
 * retry 接口对 `Pending` / `Retrying` / `Processing` 返回当前行（HTTP 200），
 * 只有 `DeadLetter` / `Skipped` 会被真正改回 `Pending`。这里按返回状态区分，
 * 不能对这些行统一提示“已重新排队”。
 */
export function retryDeliveryMessage(status: string): string {
  switch (status) {
    case 'Pending':
      return '投递已在待发送队列中，稍后自动重试'
    case 'Retrying':
      return '该投递正在自动重试队列中，本次重试未改变队列状态'
    case 'Processing':
      return '该投递正在发送中，本次重试未改变队列状态'
    case 'Delivered':
      return '该投递已送达，本次重试未改变状态'
    case 'Suppressed':
      return '该投递已抑制，本次重试未改变状态'
    default:
      return `该投递当前状态为 ${status}，本次重试未改变队列状态`
  }
}
