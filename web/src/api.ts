import type { Health, ItemDto, Location, ProviderSetting, RunDetail, RunSummary } from './types'

const KEY_STORAGE = 'groceryscout.apiKey'

export const apiKey = {
  get: () => localStorage.getItem(KEY_STORAGE) ?? '',
  set: (value: string) => (value ? localStorage.setItem(KEY_STORAGE, value) : localStorage.removeItem(KEY_STORAGE)),
}

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const key = apiKey.get()
  if (key) headers['X-Api-Key'] = key

  let resp: Response
  try {
    resp = await fetch(`/api${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  } catch {
    throw new ApiError(0, 'Cannot reach the server. Is the Grocery Price Scout API running?')
  }

  if (!resp.ok) {
    let message = resp.statusText || `Request failed (${resp.status})`
    try {
      const data = await resp.json()
      if (data?.error) message = data.error
    } catch {
      // body was not JSON; keep the status text
    }
    if (resp.status === 401) message = 'The server needs an API key. Add it in Settings.'
    throw new ApiError(resp.status, message)
  }
  const text = await resp.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const api = {
  health: () => request<Health>('GET', '/health'),
  parseList: (text: string) => request<{ items: ItemDto[] }>('POST', '/lists/parse', { text }),

  locations: () => request<Location[]>('GET', '/locations'),
  addLocation: (label: string, pincode: string, makeDefault: boolean) =>
    request<Location>('POST', '/locations', { label, pincode, makeDefault }),
  setDefaultLocation: (id: number) => request<void>('PUT', `/locations/${id}/default`),
  deleteLocation: (id: number) => request<void>('DELETE', `/locations/${id}`),

  providers: () => request<ProviderSetting[]>('GET', '/providers'),
  setProviderEnabled: (name: string, enabled: boolean) =>
    request<void>('PUT', `/providers/${encodeURIComponent(name)}/enabled`, { enabled }),

  createRun: (items: ItemDto[], pincode: string | null, addToCart: boolean, text?: string, preferPurchased = true) =>
    request<{ id: number }>('POST', '/runs', { items, pincode, addToCart, text, preferPurchased }),
  runs: () => request<RunSummary[]>('GET', '/runs'),
  run: (id: number) => request<RunDetail>('GET', `/runs/${id}`),
  cancelRun: (id: number) => request<void>('POST', `/runs/${id}/cancel`),
  addToCart: (runId: number, index: number, listing?: { provider: string; productId: string }) =>
    request<void>('POST', `/runs/${runId}/items/${index}/add-to-cart`, listing ?? {}),
  deleteRun: (id: number) => request<void>('DELETE', `/runs/${id}`),
}
