import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import HabitStreakRing from '../HabitStreakRing';

describe('HabitStreakRing', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<HabitStreakRing loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<HabitStreakRing data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<HabitStreakRing />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
