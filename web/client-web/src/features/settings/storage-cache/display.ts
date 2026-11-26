export function serverLabel(binding: string): string {
  return binding || '当前部署源'
}

export function belongsToOtherServer(draftBinding: string, currentBinding: string): boolean {
  return Boolean(currentBinding && draftBinding && draftBinding !== currentBinding)
}
