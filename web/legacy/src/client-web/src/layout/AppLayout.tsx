import { lazy, Suspense, useState } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useVersionInfo } from '../hooks/useVersionInfo';
import { useAuth } from '../auth/AuthContext';
import { CalendarVisibilityProvider } from '../context/CalendarVisibilityContext';
import QuickNoteFloatingEntry from '../components/quick-notes/QuickNoteFloatingEntry';
import Sidebar from './Sidebar';
import { PAGE_TITLE_ITEMS } from './navItems';
import InboxPanel from '../panels/InboxPanel';
import TodayPage from '../pages/TodayPage';
import CalendarPage from '../pages/CalendarPage';
import TaskListPage from '../pages/TaskListPage';
import PcTrackerPage from '../pages/PcTrackerPage';
import SettingsPage from '../pages/SettingsPage';
import AiSettingsPage from '../pages/AiSettingsPage';
import McpSettingsPage from '../pages/McpSettingsPage';
import CalendarDataManager from '../pages/CalendarDataManager';
import RecycleBinPage from '../pages/RecycleBinPage';
import PcDetailQueryPage from '../pages/PcDetailQueryPage';
import StatusPage from '../pages/StatusPage';
import AppKnowledgeBasePage from '../pages/AppKnowledgeBasePage';
import CategoryTreePage from '../pages/CategoryTreePage';
import DataReliabilityPage from '../pages/DataReliabilityPage';
import { ErrorBoundary } from '../components/error/ErrorBoundary';
import NotFoundPage from '../components/error/NotFoundPage';

const QuickNotesPage = lazy(() => import('../pages/QuickNotesPage'));
const FilesPage = lazy(() => import('../pages/FilesPage'));
const MobileRecordsPage = lazy(() => import('../pages/MobileRecordsPage'));
const HistoricalLocationPage = lazy(() => import('../pages/HistoricalLocationPage'));
const WorkbenchPage = lazy(() => import('../pages/WorkbenchPage'));
const SyncPage = lazy(() => import('../pages/SyncPage'));
const DeviceManagementPage = lazy(() => import('../pages/DeviceManagementPage'));
const DeviceDetailPage = lazy(() => import('../pages/DeviceDetailPage'));
const DataCenterPage = lazy(() => import('../pages/DataCenterPage'));
const ConfirmationsPage = lazy(() => import('../pages/ConfirmationsPage'));
const RemindersPage = lazy(() => import('../pages/RemindersPage'));
const ReportsPage = lazy(() => import('../pages/ReportsPage'));
const HabitsPage = lazy(() => import('../pages/HabitsPage'));
const AuditTimelinePage = lazy(() => import('../pages/AuditTimelinePage'));
const EndpointShellPage = lazy(() => import('../pages/EndpointShellPage'));
const ExhibitionPage = lazy(() => import('../pages/ExhibitionPage'));
const AdminUsersPage = lazy(() => import('../pages/AdminUsersPage'));
const PcBrowserSitePage = lazy(() => import('../pages/PcBrowserSitePage'));

function SuspenseFallback() {
  return <div className="h-full" aria-busy="true" />;
}

export default function AppLayout() {
  const { isAuthenticated } = useAuth();
  const location = useLocation();
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />;
  }

  // 标题查找用 PAGE_TITLE_ITEMS（含已移入设置页的板块），避免这些页面头部退化成默认标题。
  const currentNavItem = PAGE_TITLE_ITEMS.find(
    (item) => location.pathname === item.path || location.pathname.startsWith(`${item.path}/`)
  );
  const pageTitle = currentNavItem?.label ?? '个人中枢';

  const showCalendarInbox = location.pathname === '/calendar' || location.pathname.startsWith('/calendar/');
  const { localVersion, serverVersion, latestVersion, hasUpdate } = useVersionInfo();

  return (
    <CalendarVisibilityProvider>
      <div className="pim-shell h-screen flex flex-col md:flex-row overflow-hidden">
        {/* Mobile Top Header */}
        <header className="flex h-12 shrink-0 items-center justify-between border-b border-slate-200/80 bg-white/95 px-3 backdrop-blur-sm md:hidden">
          <button
            type="button"
            onClick={() => setMobileMenuOpen(true)}
            className="flex h-9 w-9 items-center justify-center rounded-lg text-slate-600 hover:bg-slate-100 hover:text-slate-900"
            aria-label="打开主菜单"
          >
            <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 12h16M4 18h16" />
            </svg>
          </button>
          <div className="flex items-center gap-2">
            <span className="text-sm font-semibold text-slate-800">{pageTitle}</span>
          </div>
          <div className="w-9" aria-hidden="true" />
        </header>

        <Sidebar mobileOpen={mobileMenuOpen} onClose={() => setMobileMenuOpen(false)} />
        <main className="pim-route-surface flex-1 min-w-0 min-h-0 h-full overflow-y-auto p-4">
          <ErrorBoundary key={location.pathname} resetKeys={[location.pathname]}>
            <Suspense fallback={<SuspenseFallback />}>
              <Routes>
                <Route path="/today" element={<TodayPage />} />
                <Route path="/calendar" element={<CalendarPage />} />
                <Route path="/workbench" element={<WorkbenchPage />} />
                <Route path="/sync" element={<Navigate to="/settings/sync" replace />} />
                <Route path="/data-center" element={<DataCenterPage />} />
                <Route path="/confirmations" element={<ConfirmationsPage />} />
                <Route path="/reminders" element={<RemindersPage />} />
                <Route path="/reports" element={<ReportsPage />} />
                <Route path="/habits" element={<HabitsPage />} />
                <Route path="/exhibition" element={<ExhibitionPage />} />
                <Route path="/audit/:objectType/:objectId" element={<AuditTimelinePage />} />
                <Route path="/endpoint-shell" element={<EndpointShellPage />} />
                <Route path="/quick-notes" element={<QuickNotesPage />} />
                <Route path="/files" element={<FilesPage />} />
                <Route path="/timeline" element={<Navigate to="/calendar?view=timeline" replace />} />
                <Route path="/week" element={<Navigate to="/calendar?view=timeline" replace />} />
                <Route path="/month" element={<Navigate to="/calendar?view=month" replace />} />
                <Route path="/tasks" element={<TaskListPage />} />
                <Route path="/pc-tracker" element={<PcTrackerPage />} />
                <Route path="/pc-tracker/browser" element={<PcBrowserSitePage />} />
                <Route path="/mobile-records" element={<MobileRecordsPage />} />
                <Route path="/location-history" element={<HistoricalLocationPage />} />
                <Route path="/devices" element={<DeviceManagementPage />} />
                <Route path="/devices/:deviceId" element={<DeviceDetailPage />} />
                <Route path="/status" element={<StatusPage />} />
                <Route path="/settings" element={<SettingsPage />} />
                <Route path="/settings/data-reliability" element={<DataReliabilityPage />} />
                <Route path="/settings/sync" element={<SyncPage />} />
                <Route path="/settings/ai" element={<AiSettingsPage />} />
                <Route path="/settings/mcp" element={<McpSettingsPage />} />
                <Route path="/settings/calendar-data" element={<CalendarDataManager />} />
                <Route path="/settings/recycle-bin" element={<RecycleBinPage />} />
                <Route path="/settings/pc-data" element={<PcDetailQueryPage />} />
                <Route path="/settings/users" element={<AdminUsersPage />} />
                <Route path="/app-knowledge-base" element={<AppKnowledgeBasePage />} />
                <Route path="/app-knowledge-base/categories" element={<CategoryTreePage />} />
                <Route path="/pc-categories" element={<Navigate to="/app-knowledge-base/categories" replace />} />
                <Route path="/pc-classification" element={<Navigate to="/app-knowledge-base" replace />} />
                <Route path="*" element={<NotFoundPage />} />
              </Routes>
            </Suspense>
          </ErrorBoundary>
          <footer className="mt-6 flex gap-3 border-t border-slate-100 pt-3 text-xs text-slate-400">
            <span>v{localVersion}</span><span>API v{serverVersion ?? '...'}</span>{hasUpdate && <span className="text-amber-600">有新版 v{latestVersion}</span>}
          </footer>
        </main>
        {showCalendarInbox && <InboxPanel draggable />}
        {/* 全局快速记录入口（#280）：每页有且只有一个；/quick-notes 页有自己的黑色按钮，故不渲染。 */}
        <QuickNoteFloatingEntry pathname={location.pathname} />
      </div>
    </CalendarVisibilityProvider>
  );
}
