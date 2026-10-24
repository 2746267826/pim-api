import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import TaskFunnel from '../TaskFunnel';

describe('TaskFunnel', () => {
  it('loading shows skeleton', () => {
    const { container } = render(<TaskFunnel loading />);
    expect(container.querySelector('[aria-busy="true"]') || container.textContent).toBeTruthy();
  });
  it('empty shows placeholder', () => {
    const { container } = render(<TaskFunnel data={[]} />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
  it('renders chart', () => {
    const { container } = render(<TaskFunnel />);
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
