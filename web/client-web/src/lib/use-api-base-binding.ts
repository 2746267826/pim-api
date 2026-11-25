import { useEffect, useState } from 'react'
import { getApiBase } from './apiBase'

/** Reactive API-base binding for cache keys that must not cross server identities. */
export function useApiBaseBinding(): string {
  const [binding, setBinding] = useState(() => getApiBase())

  useEffect(() => {
    const update = () => setBinding(getApiBase())
    window.addEventListener('pim:api-base-changed', update)
    return () => window.removeEventListener('pim:api-base-changed', update)
  }, [])

  return binding
}
