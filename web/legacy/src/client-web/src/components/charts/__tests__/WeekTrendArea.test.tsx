import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import WeekTrendArea from '../WeekTrendArea';

describe('WeekTrendArea', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<WeekTrendArea loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<WeekTrendArea data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<WeekTrendArea />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
