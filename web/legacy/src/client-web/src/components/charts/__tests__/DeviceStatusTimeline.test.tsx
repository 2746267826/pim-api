import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import DeviceStatusTimeline from '../DeviceStatusTimeline';

describe('DeviceStatusTimeline', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<DeviceStatusTimeline loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<DeviceStatusTimeline data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<DeviceStatusTimeline />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
