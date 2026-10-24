import { Routes, Route, Navigate } from 'react-router-dom'
import { AuthProvider } from './auth/AuthContext'
import LoginPage from './auth/LoginPage'
import AppLayout from './layout/AppLayout'
import AndroidEmbedLayout from './layout/AndroidEmbedLayout'
import AndroidTodayEmbedPage from './pages/AndroidTodayEmbedPage'
import HistoricalLocationPage from './pages/HistoricalLocationPage'

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/embed/android/today" element={<AndroidEmbedLayout><AndroidTodayEmbedPage /></AndroidEmbedLayout>} />
      <Route path="/embed/android/tracks" element={<AndroidEmbedLayout><HistoricalLocationPage embedded /></AndroidEmbedLayout>} />
      <Route path="/*" element={<AuthProvider><Routes><Route path="/login" element={<LoginPage />} /><Route path="/" element={<Navigate to="/today" replace />} /><Route path="/*" element={<AppLayout />} /></Routes></AuthProvider>} />
    </Routes>
  )
}

export default function App() {
  return <AppRoutes />
}
