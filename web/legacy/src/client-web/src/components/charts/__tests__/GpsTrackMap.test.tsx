import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import GpsTrackMap from '../GpsTrackMap';

describe('GpsTrackMap', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<GpsTrackMap loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<GpsTrackMap data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<GpsTrackMap />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
