import { NavLink } from 'react-router-dom';
import { NAV_ITEMS } from './navItems';

const MOBILE_NAV_PATHS = ['/today', '/calendar', '/quick-notes', '/tasks', '/reminders'];

/**
 * @deprecated 方案 B 已全面采用顶部轻量 Header 与侧滑抽屉 Drawer 替代移动端底部导航栏。
 */
export default function MobileNav() {
  const items = NAV_ITEMS.filter((item) => MOBILE_NAV_PATHS.includes(item.path));
  return (
    <nav
      className="fixed inset-x-0 bottom-0 z-40 flex border-t border-gray-200 bg-white md:hidden"
      style={{ paddingBottom: 'env(safe-area-inset-bottom)' }}
      aria-label="主导航"
    >
      {items.map((item) => (
        <NavLink
          key={item.path}
          to={item.path}
          className={({ isActive }) =>
            `flex flex-1 flex-col items-center justify-center py-2 text-xs ${isActive ? 'text-blue-600 font-medium' : 'text-gray-500'}`
          }
        >
          <span aria-hidden="true">{item.short}</span>
          <span>{item.label}</span>
        </NavLink>
      ))}
    </nav>
  );
}
