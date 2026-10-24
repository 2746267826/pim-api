import { useEffect, useState } from 'react'
import { getVersion } from '../api/version'

declare const __APP_VERSION__: string
declare const __GIT_SHA__: string

function parseN(v:string|null|undefined): number|null {
  if(!v) return null
  const clean = v.trim().split('+')[0].split('-')[0]
  const last = clean.split('.').pop()!
  const n = parseInt(last,10); return Number.isNaN(n)? null : n
}

export function useVersionInfo(){
  const localVersion = (typeof __APP_VERSION__ !== 'undefined' ? __APP_VERSION__ : (import.meta.env.VITE_APP_VERSION as string)) || '0.0.0-local'
  const localSha = (typeof __GIT_SHA__ !== 'undefined' ? __GIT_SHA__ : (import.meta.env.VITE_GIT_SHA as string)) || 'local'
  const [server,setServer]=useState<{version:string,latest:string|null,checkedAt:string|null,error:string|null}|null>(null)
  useEffect(()=>{
    try {
      if (typeof window !== 'undefined' && localStorage.getItem('accessToken') === 'schedule-workbench-visual-audit-token') return;
    } catch {}
    getVersion().then(r=>setServer({version:r.version, latest:r.latestVersion, checkedAt:r.checkedAt, error:r.error})).catch(e=>setServer({version:'unknown',latest:null,checkedAt:null,error:String(e)}))
  },[])
  const hasUpdate = server && parseN(server.latest) !== null && parseN(server.version) !== null ? (parseN(server.latest)! > parseN(server.version)!) : false
  return { localVersion, localSha, serverVersion: server?.version ?? null, latestVersion: server?.latest ?? null, checkedAt: server?.checkedAt ?? null, error: server?.error ?? null, hasUpdate: !!hasUpdate }
}
