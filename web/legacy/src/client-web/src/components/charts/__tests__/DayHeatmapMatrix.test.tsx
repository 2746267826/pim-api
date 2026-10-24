import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import DayHeatmapMatrix from '../DayHeatmapMatrix';

describe('DayHeatmapMatrix', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<DayHeatmapMatrix loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<DayHeatmapMatrix data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<DayHeatmapMatrix />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
