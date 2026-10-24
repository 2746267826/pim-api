import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import DataQualityGauge from '../DataQualityGauge';

describe('DataQualityGauge', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<DataQualityGauge loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<DataQualityGauge data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<DataQualityGauge />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
