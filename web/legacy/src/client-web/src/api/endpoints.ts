import { apiGet, apiPost } from './client';
import type {
  ApiResponse,
  EndpointCollectionQuality,
  EndpointHeartbeatRequest,
  EndpointNotificationActionRequest,
  EndpointNotificationActionResponse,
  EndpointStatus,
} from '../types';

export const endpointApiPaths = {
  list() {
    return '/endpoints';
  },
  heartbeat(deviceId: string) {
    return `/endpoints/${encodeURIComponent(deviceId)}/heartbeat`;
  },
  collectionQuality(deviceId: string) {
    return `/endpoints/${encodeURIComponent(deviceId)}/collection-quality`;
  },
  notificationActions(deviceId: string) {
    return `/endpoints/${encodeURIComponent(deviceId)}/notification-actions`;
  },
} as const;

export async function listEndpointStatuses() {
  const r = await apiGet<ApiResponse<EndpointStatus[]>>(endpointApiPaths.list());
  return r.data;
}

export async function heartbeatEndpoint(deviceId: string, data: EndpointHeartbeatRequest) {
  const r = await apiPost<ApiResponse<EndpointStatus>>(
    endpointApiPaths.heartbeat(deviceId),
    data
  );
  return r.data;
}

export async function getEndpointCollectionQuality(deviceId: string) {
  const r = await apiGet<ApiResponse<EndpointCollectionQuality>>(
    endpointApiPaths.collectionQuality(deviceId)
  );
  return r.data;
}

export async function handleEndpointNotificationAction(
  deviceId: string,
  data: EndpointNotificationActionRequest
) {
  const r = await apiPost<ApiResponse<EndpointNotificationActionResponse>>(
    endpointApiPaths.notificationActions(deviceId),
    data
  );
  return r.data;
}
