import { useVersionInfo } from '../hooks/useVersionInfo'
export default function AboutPimCard(){
  const { localVersion, localSha, serverVersion, latestVersion, checkedAt, error, hasUpdate } = useVersionInfo()
  const rows = [
    { label:'Web', value:`${localVersion} (${localSha})` },
    { label:'API', value: serverVersion ? `${serverVersion}` : '加载中' },
    { label:'GitHub 最新', value: latestVersion ?? '检查中' },
    { label:'Windows Daemon', value:'由托盘上报' },
    { label:'Windows Shell', value:'由 Shell 上报' },
    { label:'Android', value:'由 App 上报' },
  ]
  const copy = ()=> navigator.clipboard.writeText(`PIM versions:\nweb=${localVersion} sha=${localSha}\napi=${serverVersion}\nlatest=${latestVersion} checkedAt=${checkedAt} error=${error||'none'}`)
  return <div className="pim-card p-5 space-y-3">
    <div className="flex justify-between"><h3 className="font-semibold">关于 PIM</h3><button onClick={copy} className="text-xs border px-2 py-1 rounded">复制版本信息</button></div>
    {hasUpdate && <div className="bg-amber-50 border border-amber-200 text-amber-800 text-sm px-3 py-2 rounded">服务端有新版 v{latestVersion}</div>}
    {error && <div className="text-xs text-rose-600">检查失败：{error}（{checkedAt}）</div>}
    {rows.map(r=> <div key={r.label} className="flex justify-between text-sm"><span className="text-slate-500">{r.label}</span><span className="font-mono">{r.value}</span></div>)}
    {checkedAt && <div className="text-xs text-slate-400">检查时间：{new Date(checkedAt).toLocaleString()}</div>}
  </div>
}
