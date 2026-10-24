import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import Sidebar from '../Sidebar';
import { CalendarVisibilityProvider } from '../../context/CalendarVisibilityContext';

vi.mock('../../auth/AuthContext', () => ({
  useAuth: () => ({
    logout: vi.fn(),
    username: 'testuser',
  }),
}));

vi.mock('../../api/calendar', () => ({
  getCalendars: vi.fn().mockResolvedValue([]),
  createCalendar: vi.fn(),
  updateCalendar: vi.fn(),
  deleteCalendar: vi.fn(),
  previewCalendarDelete: vi.fn(),
}));

vi.mock('../../components/status/SidebarStatusIndicator', () => ({
  default: () => <div data-testid="status-indicator">Status OK</div>,
}));

function renderSidebar(props: { mobileOpen?: boolean; onClose?: () => void } = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <CalendarVisibilityProvider>
        <MemoryRouter initialEntries={['/today']}>
          <Sidebar {...props} />
        </MemoryRouter>
      </CalendarVisibilityProvider>
    </QueryClientProvider>
  );
}

describe('Sidebar drawer interaction', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders desktop sidebar normally', () => {
    renderSidebar();
    expect(screen.getByText('个人中枢')).toBeInTheDocument();
    expect(screen.getByText('今日')).toBeInTheDocument();
  });

  it('calls onClose when backdrop overlay is clicked in mobile drawer mode', () => {
    const onClose = vi.fn();
    renderSidebar({ mobileOpen: true, onClose });
    
    const backdrop = screen.getByTestId('sidebar-backdrop');
    expect(backdrop).toBeInTheDocument();
    fireEvent.click(backdrop);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when mobile close button is clicked', () => {
    const onClose = vi.fn();
    renderSidebar({ mobileOpen: true, onClose });
    
    const closeBtn = screen.getByLabelText('关闭主菜单');
    expect(closeBtn).toBeInTheDocument();
    fireEvent.click(closeBtn);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when a navigation item is clicked', () => {
    const onClose = vi.fn();
    renderSidebar({ mobileOpen: true, onClose });
    
    const calendarItem = screen.getByRole('button', { name: '日历' });
    fireEvent.click(calendarItem);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when Escape key is pressed', () => {
    const onClose = vi.fn();
    renderSidebar({ mobileOpen: true, onClose });
    
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
