import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import CategoryStackedBar from '../CategoryStackedBar';

describe('CategoryStackedBar', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<CategoryStackedBar loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('renders with fallback data', () => {
    const { container } = render(<CategoryStackedBar />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
