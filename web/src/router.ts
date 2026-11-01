import { useEffect, useState } from 'react'

export type Route =
  | { name: 'new' }
  | { name: 'runs' }
  | { name: 'run'; id: number }
  | { name: 'settings' }

export function parseHash(hash: string): Route {
  const path = hash.replace(/^#\/?/, '')
  if (path === 'runs') return { name: 'runs' }
  if (path === 'settings') return { name: 'settings' }
  const m = /^runs\/(\d+)$/.exec(path)
  if (m) return { name: 'run', id: Number(m[1]) }
  return { name: 'new' }
}

export function navigate(path: string) {
  window.location.hash = `#/${path}`
}

export function useRoute(): Route {
  const [route, setRoute] = useState<Route>(() => parseHash(window.location.hash))
  useEffect(() => {
    const onChange = () => setRoute(parseHash(window.location.hash))
    window.addEventListener('hashchange', onChange)
    return () => window.removeEventListener('hashchange', onChange)
  }, [])
  return route
}
