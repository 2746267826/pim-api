export function readMediaFile(file: File, signal: AbortSignal): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    const abort = () => reader.abort()
    const clean = () => signal.removeEventListener('abort', abort)
    if (signal.aborted) { reject(new DOMException('已取消', 'AbortError')); return }
    signal.addEventListener('abort', abort, { once: true })
    reader.onload = () => { clean(); const value = String(reader.result); resolve(value.slice(value.indexOf(',') + 1)) }
    reader.onerror = () => { clean(); reject(new Error('材料读取失败，请重新选择')) }
    reader.onabort = () => { clean(); reject(new DOMException('已取消', 'AbortError')) }
    reader.readAsDataURL(file)
  })
}
