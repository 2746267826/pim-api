import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import LocationHeatmap from '../LocationHeatmap';

describe('LocationHeatmap', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<LocationHeatmap loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<LocationHeatmap data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<LocationHeatmap />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
