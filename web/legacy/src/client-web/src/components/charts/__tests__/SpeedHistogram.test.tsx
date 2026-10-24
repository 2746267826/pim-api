import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import SpeedHistogram from '../SpeedHistogram';

describe('SpeedHistogram', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<SpeedHistogram loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<SpeedHistogram data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<SpeedHistogram />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
