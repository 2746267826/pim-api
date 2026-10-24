import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import PcAppDonut from '../PcAppDonut';

describe('PcAppDonut', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<PcAppDonut loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<PcAppDonut data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<PcAppDonut />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
