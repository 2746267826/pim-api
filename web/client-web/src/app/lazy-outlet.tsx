import { Suspense } from 'react'
import { Outlet } from 'react-router'
import { Skeleton } from '@/components/ui'

/** 懒加载页面的通用兜底：与常见页面结构近似的骨架（页头 + 卡片栅格），避免闪白 */
export function RouteFallback() {
  return (
    <div className="space-y-4">
      <div className="space-y-2">
        <Skeleton className="h-7 w-44" />
        <Skeleton className="h-4 w-64" />
      </div>
      <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} className="h-36" />
        ))}
      </div>
    </div>
  )
}

/** 懒加载路由的统一挂起点：所有 React.lazy 页面都在这里被 Suspense 捕获 */
export function LazyOutlet() {
  return (
    <Suspense fallback={<RouteFallback />}>
      <Outlet />
    </Suspense>
  )
}
