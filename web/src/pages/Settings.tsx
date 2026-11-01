import { useCallback, useEffect, useState } from 'react'
import { api, apiKey, ApiError } from '../api'
import type { Health, Location, ProviderSetting } from '../types'

export default function Settings() {
  const [health, setHealth] = useState<Health | null>(null)
  const [locations, setLocations] = useState<Location[]>([])
  const [providers, setProviders] = useState<ProviderSetting[]>([])
  const [error, setError] = useState<string | null>(null)
  const [label, setLabel] = useState('')
  const [pincode, setPincode] = useState('')
  const [key, setKey] = useState(apiKey.get())

  const load = useCallback(async () => {
    try {
      const [h, l, p] = await Promise.all([api.health(), api.locations(), api.providers()])
      setHealth(h); setLocations(l); setProviders(p); setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not load settings.')
    }
  }, [])

  useEffect(() => { void load() }, [load])

  async function run(fn: () => Promise<unknown>) {
    try { await fn(); await load() } catch (e) { setError(e instanceof ApiError ? e.message : 'That did not work.') }
  }

  return (
    <section>
      <h1>Settings</h1>
      {error && <p className="banner error" role="alert">{error}</p>}

      <div className="card">
        <h2>Status</h2>
        {health ? (
          <ul className="status">
            <li className={health.browser ? 'ok' : 'bad'}>
              Browser {health.browser ? 'connected' : 'not running'}
              {!health.browser && <small>Start it with <code>tools\start-chrome.ps1</code> and log in to the stores.</small>}
            </li>
            <li className={health.ollama ? 'ok' : health.llmEnabled ? 'bad' : 'off'}>
              {health.llmEnabled ? (health.ollama ? `Smart list reading on (${health.llmModel})` : 'Ollama not running (using simple list reading)') : 'Smart list reading off'}
            </li>
            <li className={health.hasDefaultLocation ? 'ok' : 'bad'}>{health.hasDefaultLocation ? 'Default location set' : 'No location yet'}</li>
          </ul>
        ) : <p className="muted">Checking…</p>}
      </div>

      <div className="card">
        <h2>Delivery locations</h2>
        {locations.map(l => (
          <div className="row between" key={l.id}>
            <span>{l.label} · {l.pincode} {l.isDefault && <span className="badge">default</span>}</span>
            <span className="row">
              {!l.isDefault && <button className="link" onClick={() => run(() => api.setDefaultLocation(l.id))}>Make default</button>}
              <button className="link danger" onClick={() => run(() => api.deleteLocation(l.id))}>Remove</button>
            </span>
          </div>
        ))}
        <form className="row" onSubmit={e => { e.preventDefault(); void run(async () => { await api.addLocation(label.trim(), pincode, locations.length === 0); setLabel(''); setPincode('') }) }}>
          <input placeholder="Label (Home)" value={label} onChange={e => setLabel(e.target.value)} aria-label="Label" />
          <input placeholder="Pincode" inputMode="numeric" maxLength={6} value={pincode} onChange={e => setPincode(e.target.value.replace(/\D/g, ''))} aria-label="Pincode" />
          <button className="primary" disabled={!label.trim() || pincode.length !== 6}>Add</button>
        </form>
        <p className="muted">The stores use the delivery address set in the logged-in browser; this pincode is checked against it.</p>
      </div>

      <div className="card">
        <h2>Stores</h2>
        {providers.map(p => (
          <label className="check" key={p.name}>
            <input type="checkbox" checked={p.enabled} onChange={e => run(() => api.setProviderEnabled(p.name, e.target.checked))} />
            <span>{p.name}</span>
          </label>
        ))}
      </div>

      <div className="card">
        <h2>Access key</h2>
        <p className="muted">Only needed if the server was started with an API key.</p>
        <form className="row" onSubmit={e => { e.preventDefault(); apiKey.set(key.trim()); void load() }}>
          <input type="password" value={key} onChange={e => setKey(e.target.value)} placeholder="API key" aria-label="API key" autoComplete="off" />
          <button>Save</button>
        </form>
      </div>
    </section>
  )
}
