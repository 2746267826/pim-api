let lastInteractionTime = 0;

export function notifyUserInteraction(): void {
  lastInteractionTime = Date.now();
}

export function getAutoRefreshInterval(now: Date = new Date()): number {
  return now.getHours() >= 6 ? 300000 : 1800000;
}

export function getDeferredAutoRefreshInterval(): number {
  return Date.now() - lastInteractionTime < 500 ? 1000 : getAutoRefreshInterval(new Date());
}

export function installInteractionDeferral(): void {
  if (typeof window !== 'undefined') {
    window.addEventListener('scroll', notifyUserInteraction, { capture: true, passive: true });
  }
}

export function resetInteractionStateForTests(): void {
  lastInteractionTime = 0;
}
