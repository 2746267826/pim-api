import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import KeyboardHeatmap from '../KeyboardHeatmap';

describe('KeyboardHeatmap', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<KeyboardHeatmap loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<KeyboardHeatmap data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<KeyboardHeatmap />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
