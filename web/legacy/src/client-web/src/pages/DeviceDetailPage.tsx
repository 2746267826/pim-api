import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { getDeviceDetail } from '../api/mobile';

export default function DeviceDetailPage(){
  const { deviceId } = useParams();
  const { data, isLoading } = useQuery({ queryKey: ['device-detail', deviceId], queryFn: ()=> getDeviceDetail(deviceId!), enabled: !!deviceId });
  if (isLoading) return <div className="p-4">加载中...</div>;
  if (!data) return <div className="p-4">无数据</div>;
  const d = data.device;
  return <div className="p-4 space-y-4">
    <h1 className="text-xl font-semibold">设备详情 {d.displayName||d.deviceId}</h1>
    <div className="text-sm space-y-1">
      <div>型号: {d.brand} {d.model} 系统:{d.osVersion} App:{d.appVersion}</div>
      <div>ID: {d.deviceId}</div>
      <div>注册: {d.registeredAtUtc} 最后: {d.lastSeenAtUtc}</div>
    </div>
    <div>
      <h2 className="font-medium">同步历史</h2>
      <ul className="text-xs space-y-1">{(data.syncHistory??[]).map((h:any)=><li key={h.batchId}>{h.batchId} {h.createdAt} {h.acceptedCount} {h.status}</li>)}</ul>
    </div>
    <div>
      <h2 className="font-medium">健康时间线(7天)</h2>
      <div className="text-xs">{JSON.stringify(data.healthTimeline)}</div>
    </div>
    <div>
      <h2 className="font-medium">存储明细</h2>
      <pre className="text-xs bg-slate-50 p-2 overflow-auto">{JSON.stringify(data.stats, null, 2)}</pre>
    </div>
  </div>;
}
