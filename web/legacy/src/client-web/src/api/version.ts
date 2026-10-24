export type VersionResponse = { version:string; capabilities:string[]; latestVersion:string|null; checkedAt:string|null; error:string|null }
export async function getVersion(): Promise<VersionResponse> {
  const r = await fetch('/api/version'); if(!r.ok) throw new Error(`GET /api/version ${r.status}`); return r.json()
}
export type LatestResponse = { windowsVersion:string|null; windowsUrl:string|null; androidVersion:string|null; androidUrl:string|null; checkedAt:string|null; error:string|null }
export async function getClientLatest(): Promise<LatestResponse> {
  const r = await fetch('/api/client/shell/latest'); if(!r.ok) throw new Error(`GET /api/client/shell/latest ${r.status}`); return r.json()
}
