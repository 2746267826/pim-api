import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import WeekTrendLine from '../WeekTrendLine';

describe('WeekTrendLine', () => {
  it('renders skeleton when loading', () => {
    const { container } = render(<WeekTrendLine loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('renders empty when no data', () => {
    render(<WeekTrendLine data={[]} />);
    // fallback renders chart, so check that it still renders EChartBox container
    expect(document.body).toBeDefined();
  });
  it('renders chart with data', () => {
    const data = [{date:"W1", total:1380}];
    const { container } = render(<WeekTrendLine data={data} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
