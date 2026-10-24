import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import AppLayout from '../AppLayout';

vi.mock('../../auth/AuthContext', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    username: 'testuser',
    logout: vi.fn(),
  }),
}));

vi.mock('../../hooks/useVersionInfo', () => ({
  useVersionInfo: () => ({
    localVersion: '0.0.0-test',
    serverVersion: '0.0.0-test',
    latestVersion: '0.0.0-test',
    hasUpdate: false,
  }),
}));

vi.mock('../Sidebar', () => ({
  default: ({ mobileOpen, onClose }: { mobileOpen?: boolean; onClose?: () => void }) => (
    <aside data-testid="mock-sidebar" data-mobile-open={mobileOpen ? 'true' : 'false'}>
      <button data-testid="sidebar-close-btn" onClick={onClose}>
        Mock Close
      </button>
    </aside>
  ),
}));

vi.mock('../../components/quick-notes/QuickNoteFloatingEntry', () => ({
  default: () => <button data-testid="mock-fab">+</button>,
}));

vi.mock('../../pages/TodayPage', () => ({
  default: () => <div data-testid="mock-today-page">Today Page Content</div>,
}));

vi.mock('../../pages/CalendarPage', () => ({
  default: () => <div data-testid="mock-calendar-page">Calendar Page Content</div>,
}));

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

function renderAppLayout(initialPath: string) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialPath]}>
        <AppLayout />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('AppLayout mobile navigation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders mobile top header with hamburger button and current page title', () => {
    renderAppLayout('/today');

    const hamburger = screen.getByLabelText('打开主菜单');
    expect(hamburger).toBeInTheDocument();
    expect(screen.getByText('今日')).toBeInTheDocument();

    // MobileNav bottom nav should NOT exist
    expect(screen.queryByLabelText('主导航')).toBeNull();
  });

  it('toggles mobile sidebar drawer open state when hamburger button is clicked', () => {
    renderAppLayout('/today');

    const sidebar = screen.getByTestId('mock-sidebar');
    expect(sidebar.getAttribute('data-mobile-open')).toBe('false');

    const hamburger = screen.getByLabelText('打开主菜单');
    fireEvent.click(hamburger);

    expect(sidebar.getAttribute('data-mobile-open')).toBe('true');

    // Clicking close in sidebar closes drawer
    const closeBtn = screen.getByTestId('sidebar-close-btn');
    fireEvent.click(closeBtn);
    expect(sidebar.getAttribute('data-mobile-open')).toBe('false');
  });

  it('displays correct page title for sub-routes like /calendar', () => {
    renderAppLayout('/calendar');

    expect(screen.getByText('日历')).toBeInTheDocument();
  });
});

