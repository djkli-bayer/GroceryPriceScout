import { useEffect, useState } from 'react'
import { api, ApiError } from '../api'
import { UNIT_OPTIONS } from '../format'
import { navigate } from '../router'
import type { ItemDto, Location, SizeUnit } from '../types'

const SAMPLE = 'turmeric powder 200g\nbasmati rice 5kg brand: India Gate\nmilk 500ml x2\nhalf a dozen eggs'

/** Quantity box that may be emptied while typing; only a valid 1-99 number is passed on, and it resets on blur. */
function CountInput({ value, onChange }: { value: number; onChange: (n: number) => void }) {
  const [text, setText] = useState(String(value))
  const [shown, setShown] = useState(value)
  if (value !== shown) { // the row's value changed from outside (e.g. another row was removed)
    setShown(value)
    setText(String(value))
  }

  return (
    <input
      className="num small" type="number" min="1" max="99" aria-label="Quantity" value={text}
      onChange={e => {
        setText(e.target.value)
        const n = Number(e.target.value)
        if (Number.isInteger(n) && n >= 1 && n <= 99) onChange(n)
      }}
      onBlur={() => setText(String(value))}
    />
  )
}

export default function NewRun() {
  const [text, setText] = useState('')
  const [items, setItems] = useState<ItemDto[] | null>(null)
  const [locations, setLocations] = useState<Location[]>([])
  const [pincode, setPincode] = useState('')
  const [addToCart, setAddToCart] = useState(false)
  const [preferPurchased, setPreferPurchased] = useState(true)
  const [busy, setBusy] = useState<'parse' | 'start' | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.locations().then(locs => {
      setLocations(locs)
      const def = locs.find(l => l.isDefault) ?? locs[0]
      if (def) setPincode(def.pincode)
    }).catch(() => { /* surfaced when the user acts */ })
  }, [])

  async function parse() {
    setBusy('parse'); setError(null)
    try {
      const res = await api.parseList(text)
      setItems(res.items)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not read the list.')
    } finally {
      setBusy(null)
    }
  }

  async function start() {
    if (!items?.length) return
    setBusy('start'); setError(null)
    try {
      const { id } = await api.createRun(items, pincode || null, addToCart, text, preferPurchased)
      navigate(`runs/${id}`)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not start the run.')
      setBusy(null)
    }
  }

  const update = (i: number, patch: Partial<ItemDto>) =>
    setItems(items!.map((it, idx) => (idx === i ? { ...it, ...patch } : it)))

  const validPincode = /^\d{6}$/.test(pincode)

  return (
    <section>
      <h1>New price check</h1>
      <p className="muted">Paste your list, one item per line. Example: <code>turmeric powder 200g</code>, <code>milk 500ml x2</code>.</p>

      <textarea
        value={text}
        onChange={e => { setText(e.target.value); setItems(null) }}
        rows={8}
        placeholder={SAMPLE}
        aria-label="Grocery list"
      />
      <div className="row">
        <button className="primary" onClick={parse} disabled={!text.trim() || busy !== null}>
          {busy === 'parse' ? 'Reading…' : 'Read list'}
        </button>
        <button className="link" onClick={() => { setText(SAMPLE); setItems(null) }}>Use an example</button>
      </div>

      {error && <p className="banner error" role="alert">{error}</p>}

      {items && (
        <div className="card">
          <h2>Check what I understood ({items.length})</h2>
          {items.length === 0 && <p className="muted">I couldn't find any items in that text.</p>}
          <div className="items">
            {items.map((it, i) => (
              <div className="item-edit" key={i}>
                <input
                  className="grow" value={it.item} aria-label="Item"
                  onChange={e => update(i, { item: e.target.value })}
                />
                <input
                  className="num" type="number" min="0" step="any" placeholder="size" aria-label="Size"
                  value={it.sizeValue ?? ''}
                  onChange={e => update(i, {
                    sizeValue: e.target.value === '' ? null : Number(e.target.value),
                    unit: it.unit ?? 'Gram',
                  })}
                />
                <select
                  aria-label="Unit" value={it.unit ?? 'Gram'} disabled={it.sizeValue === null}
                  onChange={e => update(i, { unit: e.target.value as SizeUnit })}
                >
                  {UNIT_OPTIONS.map(u => <option key={u.value} value={u.value}>{u.label}</option>)}
                </select>
                <label className="qty">×
                  <CountInput value={it.count} onChange={count => update(i, { count })} />
                </label>
                <input
                  className="grow" placeholder="favourite brand (optional)" aria-label="Brand" value={it.brand ?? ''}
                  onChange={e => update(i, { brand: e.target.value || null })}
                />
                <button className="icon" aria-label="Remove item" onClick={() => setItems(items.filter((_, idx) => idx !== i))}>✕</button>
              </div>
            ))}
          </div>

          <div className="options">
            <label>
              Deliver to
              {locations.length > 0 ? (
                <select value={pincode} onChange={e => setPincode(e.target.value)}>
                  {locations.map(l => <option key={l.id} value={l.pincode}>{l.label} ({l.pincode})</option>)}
                </select>
              ) : (
                <input
                  inputMode="numeric" maxLength={6} placeholder="6-digit pincode" value={pincode}
                  onChange={e => setPincode(e.target.value.replace(/\D/g, ''))}
                />
              )}
            </label>
            <label className="check">
              <input type="checkbox" checked={preferPurchased} onChange={e => setPreferPurchased(e.target.checked)} />
              <span>
                Prefer products I've bought before
                <small>{preferPurchased
                  ? 'A product marked "Purchased" by the store is chosen even if another one is cheaper (same size only).'
                  : 'Off: always the cheapest, ignoring past purchases.'}</small>
              </span>
            </label>
            <label className="check">
              <input type="checkbox" checked={addToCart} onChange={e => setAddToCart(e.target.checked)} />
              <span>
                Also add the cheapest item of each line to that store's cart
                <small>{addToCart ? 'Items will be added to your real carts. You still check out yourself.' : 'Off: price check only, nothing is added.'}</small>
              </span>
            </label>
          </div>

          <button className="primary wide" onClick={start} disabled={!items.length || !validPincode || busy !== null}>
            {busy === 'start' ? 'Starting…' : addToCart ? 'Find cheapest and add to carts' : 'Find cheapest prices'}
          </button>
          {!validPincode && <p className="muted">Enter a 6-digit pincode (or add one in Settings).</p>}
        </div>
      )}
    </section>
  )
}
