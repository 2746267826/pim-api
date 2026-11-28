import { describe, expect, it } from 'vitest'
import { retryDeliveryMessage } from './delivery-status'

describe('retryDeliveryMessage', () => {
  it('claims a requeue only for the Pending state returned after reopening', () => {
    expect(retryDeliveryMessage('Pending')).toBe('投递已在待发送队列中，稍后自动重试')
  })

  it.each(['Retrying', 'Processing'])(
    'reports that %s was not changed by this retry',
    (status) => {
      expect(retryDeliveryMessage(status)).toContain('本次重试未改变队列状态')
      expect(retryDeliveryMessage(status)).not.toContain('已重新排队')
    },
  )
})
