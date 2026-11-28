const rect = {
  x: 0,
  y: 0,
  top: 0,
  left: 0,
  right: 1,
  bottom: 1,
  width: 1,
  height: 1,
  toJSON: () => ({}),
} as DOMRect

export function installProseMirrorLayoutStubs() {
  Object.defineProperty(document, 'elementFromPoint', {
    configurable: true,
    value: () => document.body,
  })
  for (const prototype of [Element.prototype, Range.prototype]) {
    Object.defineProperty(prototype, 'getClientRects', {
      configurable: true,
      value: () => [rect],
    })
    Object.defineProperty(prototype, 'getBoundingClientRect', {
      configurable: true,
      value: () => rect,
    })
  }
}
