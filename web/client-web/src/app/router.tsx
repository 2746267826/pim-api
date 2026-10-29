import { lazy, Suspense, type ComponentType } from 'react'
import { createBrowserRouter, Navigate } from 'react-router'
import { Spinner } from '@/components/ui'
import { RequireAdmin, RequireAuth, RequireSetup } from './guards'
import { AppShell } from './shell/app-shell'
import { NotFoundPage, UnderConstruction } from '@/features/placeholders/under-construction'
import { AndroidTodayEmbedPage, AndroidTracksEmbedPage } from '@/embed/android/android-embed-pages'

/*
 * 路由级代码分包：feature 页面全部 React.lazy（ECharts/FullCalendar/TipTap/Leaflet
 * 等大依赖跟随各自页面 chunk，不再进首屏）。壳（AppShell）、守卫、404/占位、
 * Android 内嵌页保持同步（壳常驻无意义分包；内嵌页是 WebView 启动路径，避免闪变）。
 */

const lazyPage = (load: () => Promise<Record<string, unknown>>, name: string) =>
  lazy(async () => {
    const m = (await load()) as Record<string, ComponentType>
    return { default: m[name] }
  })

const LoginPage = lazyPage(() => import('@/features/auth/pages/login-page'), 'LoginPage')
const SetupPage = lazyPage(() => import('@/features/server/pages/setup-page'), 'SetupPage')
const ServerSettingsPage = lazyPage(() => import('@/features/server/pages/server-settings-page'), 'ServerSettingsPage')
const SettingsHubPage = lazyPage(() => import('@/features/settings/pages/settings-hub-page'), 'SettingsHubPage')

const TodayPage = lazyPage(() => import('@/features/today/pages/today-page'), 'TodayPage')
const CalendarPage = lazyPage(() => import('@/features/calendar/pages/calendar-page'), 'CalendarPage')
const TasksPage = lazyPage(() => import('@/features/calendar/pages/tasks-page'), 'TasksPage')
const ConfirmationsPage = lazyPage(() => import('@/features/operations/pages/confirmations-page'), 'ConfirmationsPage')
const CalendarStylesPage = lazyPage(() => import('@/features/calendar/styles/calendar-styles-page'), 'CalendarStylesPage')
const WorkbenchPage = lazyPage(() => import('@/features/workbench/pages/workbench-page'), 'WorkbenchPage')
const RemindersPage = lazyPage(() => import('@/features/calendar/pages/reminders-page'), 'RemindersPage')
const ReportsPage = lazyPage(() => import('@/features/calendar/pages/reports-page'), 'ReportsPage')
const HabitsPage = lazyPage(() => import('@/features/calendar/pages/habits-page'), 'HabitsPage')
const QuickNotesPage = lazyPage(() => import('@/features/quick-notes/pages/quick-notes-page'), 'QuickNotesPage')
const NoteDerivedPage = lazyPage(() => import('@/features/ai/note-derived/note-derived-page'), 'NoteDerivedPage')
const DataCenterPage = lazyPage(() => import('@/features/calendar/pages/data-center-page'), 'DataCenterPage')
const AuditTimelinePage = lazyPage(() => import('@/features/operations/pages/audit-timeline-page'), 'AuditTimelinePage')

const PcTrackerPage = lazyPage(() => import('@/features/pc/pages/pc-tracker-page'), 'PcTrackerPage')
const BrowserPage = lazyPage(() => import('@/features/pc/pages/browser-page'), 'BrowserPage')

const MobileRecordsPage = lazyPage(() => import('@/features/mobile/pages/mobile-records-page'), 'MobileRecordsPage')
const LocationHistoryPage = lazyPage(() => import('@/features/mobile/pages/location-history-page'), 'LocationHistoryPage')
const DevicesPage = lazyPage(() => import('@/features/mobile/pages/devices-page'), 'DevicesPage')
const DeviceDetailPage = lazyPage(() => import('@/features/mobile/pages/device-detail-page'), 'DeviceDetailPage')

const FilesPage = lazyPage(() => import('@/features/files/pages/files-page'), 'FilesPage')
const MicrosoftPage = lazyPage(() => import('@/features/settings/microsoft/microsoft-page'), 'MicrosoftPage')
const GoogleDrivePage = lazyPage(() => import('@/features/settings/google/google-page'), 'GoogleDrivePage')
const AppKnowledgeBasePage = lazyPage(() => import('@/features/knowledge/pages/app-knowledge-page'), 'AppKnowledgeBasePage')
const CategoryTreePage = lazyPage(() => import('@/features/knowledge/pages/app-knowledge-page'), 'CategoryTreePage')
const EndpointShellPage = lazyPage(() => import('@/features/endpoints/pages/endpoint-shell-page'), 'EndpointShellPage')
const StatusPage = lazyPage(() => import('@/features/operations/pages/status-page'), 'StatusPage')

/* 设置子页同文件，共享一个 chunk */
const settingsPages = () => import('@/features/settings/pages/settings-pages')
const AdminUsersPage = lazyPage(settingsPages, 'AdminUsersPage')
const AiSettingsPage = lazyPage(settingsPages, 'AiSettingsPage')
const CalendarDataManagerPage = lazyPage(settingsPages, 'CalendarDataManagerPage')
const DataReliabilityPage = lazyPage(settingsPages, 'DataReliabilityPage')
const McpSettingsPage = lazyPage(settingsPages, 'McpSettingsPage')
const PcDetailQueryPage = lazyPage(settingsPages, 'PcDetailQueryPage')
const RecycleBinPage = lazyPage(settingsPages, 'RecycleBinPage')
const AccountSecurityPage = lazyPage(() => import('@/features/settings/pages/account-security-page'), 'AccountSecurityPage')

/** 路由表（对应规格 01 §4 全量路由；P0 先通骨架与设置域，其余为占位页） */
export const router = createBrowserRouter([
  {
    path: '/setup',
    element: (
      <Suspense fallback={<div className="grid min-h-dvh place-items-center"><Spinner className="size-6" /></div>}>
        <SetupPage />
      </Suspense>
    ),
  },
  {
    element: <RequireSetup />,
    children: [
      { path: '/login', element: <LoginPage /> },

      // Android 内嵌页（免认证、无外壳；桥见 src/embed/android/bridge.ts）
      { path: '/embed/android/today', element: <AndroidTodayEmbedPage /> },
      { path: '/embed/android/tracks', element: <AndroidTracksEmbedPage /> },

      {
        element: <RequireAuth />,
        children: [
          {
            path: '/',
            element: <AppShell />,
        children: [
          { index: true, element: <Navigate to="/today" replace /> },

          // ── 工作 ──
          { path: 'today', element: <TodayPage /> },
          { path: 'calendar', element: <CalendarPage /> },
          { path: 'calendar-styles', element: <CalendarStylesPage /> },
          { path: 'workbench', element: <WorkbenchPage /> },
          { path: 'tasks', element: <TasksPage /> },
          { path: 'confirmations', element: <ConfirmationsPage /> },

          // ── 洞察 ──
          { path: 'pc-tracker', element: <PcTrackerPage /> },
          { path: 'pc-tracker/browser', element: <BrowserPage /> },
          { path: 'mobile-records', element: <MobileRecordsPage /> },
          { path: 'location-history', element: <LocationHistoryPage /> },

          // ── 收集 ──
          { path: 'quick-notes', element: <QuickNotesPage /> },
          { path: 'native-notes', element: <NoteDerivedPage /> },
          { path: 'files', element: <FilesPage /> },

          // ── 治理 ──
          { path: 'data-center', element: <DataCenterPage /> },
          { path: 'reminders', element: <RemindersPage /> },
          { path: 'reports', element: <ReportsPage /> },
          { path: 'habits', element: <HabitsPage /> },

          // ── 系统 ──
          { path: 'status', element: <StatusPage /> },
          { path: 'settings', element: <SettingsHubPage /> },
          { path: 'settings/server', element: <ServerSettingsPage /> },
          { path: 'settings/microsoft', element: <MicrosoftPage /> },
          { path: 'settings/google', element: <GoogleDrivePage /> },
          { path: 'settings/data-reliability', element: <DataReliabilityPage /> },
          { path: 'settings/account', element: <AccountSecurityPage /> },
          { path: 'settings/sync', element: <Navigate to="/settings/microsoft?tab=outlook" replace /> },
          {
            // AI 网关与用户管理为 Admin 专属（ai/admin 组 Roles="admin"），提前拦截避免 403 噪音
            element: <RequireAdmin />,
            children: [
              { path: 'settings/ai', element: <AiSettingsPage /> },
              { path: 'settings/users', element: <AdminUsersPage /> },
            ],
          },
          { path: 'settings/mcp', element: <McpSettingsPage /> },
          { path: 'settings/calendar-data', element: <CalendarDataManagerPage /> },
          { path: 'settings/recycle-bin', element: <RecycleBinPage /> },
          { path: 'settings/pc-data', element: <PcDetailQueryPage /> },

          // ── 其它 ──
          { path: 'app-knowledge-base', element: <AppKnowledgeBasePage /> },
          { path: 'app-knowledge-base/categories', element: <CategoryTreePage /> },
          { path: 'audit/:objectType/:objectId', element: <AuditTimelinePage /> },
          { path: 'endpoint-shell', element: <EndpointShellPage /> },
          { path: 'exhibition', element: <UnderConstruction title="展览馆" subtitle="图表组件陈列馆（P2 阶段实现）" /> },
          { path: 'devices', element: <DevicesPage /> },
          { path: 'devices/:deviceId', element: <DeviceDetailPage /> },

          // ── 旧别名重定向（规格 01 §4） ──
          { path: 'sync', element: <Navigate to="/settings/microsoft?tab=outlook" replace /> },
          { path: 'timeline', element: <Navigate to="/calendar?view=timeline" replace /> },
          { path: 'week', element: <Navigate to="/calendar?view=timeline" replace /> },
          { path: 'month', element: <Navigate to="/calendar?view=month" replace /> },
          { path: 'pc-categories', element: <Navigate to="/app-knowledge-base/categories" replace /> },
          { path: 'pc-classification', element: <Navigate to="/app-knowledge-base" replace /> },

          { path: '*', element: <NotFoundPage /> },
        ],
          },
        ],
      },
    ],
  },
])
