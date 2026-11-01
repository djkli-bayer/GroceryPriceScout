import { useCallback, useEffect, useState } from 'react'
import { api, ApiError } from '../api'
import { cartLabel, describeItem, formatDate, formatPrice, formatSize, matchLabel } from '../format'
import { navigate } from '../router'
import type { Listing, RunDetail, RunItem } from '../types'

const ACTIVE = ['Queued', 'Running']

export default function RunPage({ id }: { id: number }) {
  const [detail, setDetail] = useState<RunDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      setDetail(await api.run(id))
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not load the run.')
    }
  }, [id])

  const active = detail ? ACTIVE.includes(detail.run.status) : true
  const adding = detail?.items.some(i => i.outcome?.cart === 'Adding') ?? false

  useEffect(() => { void load() }, [load])

  // Poll only while something is in flight; the initial load above is separate so a state change doesn't trigger an extra reload.
  useEffect(() => {
    if (!active && !adding) return
    const timer = setInterval(load, 3000)
    return () => clearInterval(timer)
  }, [load, active, adding])

  if (!detail) return <section><h1>Run #{id}</h1>{error ? <p className="banner error">{error}</p> : <p className="muted">Loading…</p>}</section>

  const { run, items, events } = detail
  const done = items.filter(i => i.outcome)
  const total = done.reduce((sum, i) => sum + (i.outcome?.chosen ? i.outcome.chosen.price * i.count : 0), 0)
  const added = done.filter(i => i.outcome?.cart === 'Added').length
  const pct = run.itemCount ? Math.round((run.doneCount / run.itemCount) * 100) : 0

  async function act(fn: () => Promise<unknown>, after?: () => void) {
    setBusy(true)
    try { await fn(); after?.() } catch (e) { setError(e instanceof ApiError ? e.message : 'That did not work.') } finally { setBusy(false) }
  }

  const rerun = () => act(async () => {
    const res = await api.createRun(
      items.map(i => ({ item: i.item, sizeValue: i.sizeValue, unit: i.unit, count: i.count, brand: i.brand })),
      run.pincode, false, detail.rawText, run.preferPurchased)
    navigate(`runs/${res.id}`)
  })

  const addToCart = (index: number, listing?: Listing) =>
    act(() => api.addToCart(id, index, listing && { provider: listing.provider, productId: listing.productId }), load)

  return (
    <section>
      <p><a href="#/runs" className="back">← All runs</a></p>
      <h1>Run #{run.id} <span className={`badge status-${run.status}`}>{run.status}</span></h1>
      <p className="muted">
        {formatDate(run.createdUtc)} · pincode {run.pincode} · {run.addToCart ? 'adding to carts' : 'price check only'}
      </p>

      {run.status === 'Running' || run.status === 'Queued' ? (
        <div className="progress" role="progressbar" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}>
          <div style={{ width: `${pct}%` }} />
        </div>
      ) : null}
      <p>{run.doneCount} of {run.itemCount} items done
        {run.status === 'Running' && <span className="muted"> · each item takes 1–2 minutes</span>}</p>

      {run.error && <p className="banner error" role="alert">{run.error}</p>}
      {error && <p className="banner error" role="alert">{error}</p>}
      {events.map((e, i) => <p className="banner warn" key={i}>{e.message.split('\n')[0]}</p>)}

      {done.length > 0 && (
        <p className="summary">
          Estimated total <strong>{formatPrice(total)}</strong>
          {added > 0 && <> · {added} of {done.length} added to carts</>}
        </p>
      )}

      <div className="row">
        {active && <button onClick={() => act(() => api.cancelRun(id), load)} disabled={busy}>Cancel run</button>}
        {!active && <button onClick={rerun} disabled={busy}>Check prices again</button>}
        {!active && <button className="danger" onClick={() => { if (confirm('Delete this run?')) void act(() => api.deleteRun(id), () => navigate('runs')) }} disabled={busy}>Delete</button>}
      </div>

      <div className="results">
        {items.map(item => <ItemCard key={item.index} item={item} busy={busy} onAdd={addToCart} />)}
      </div>
    </section>
  )
}

type AddHandler = (index: number, listing?: Listing) => void

function ItemCard({ item, busy, onAdd }: { item: RunItem; busy: boolean; onAdd: AddHandler }) {
  const o = item.outcome
  // Adding is allowed until the item is in the cart (or being added); a failed attempt can be retried.
  const canAdd = !!o && o.cart !== 'Added' && o.cart !== 'Adding'
  return (
    <article className={`card result match-${o?.match ?? 'Pending'}`}>
      <h3>{describeItem(item)}</h3>
      {!o && <p className="muted">Waiting…</p>}
      {o && (
        <>
          <p>
            <span className={`badge match-${o.match}`}>{matchLabel[o.match]}</span>{' '}
            <span className={`badge cart-${o.cart}`}>{cartLabel[o.cart]}</span>
          </p>
          {o.chosen && <ListingRow listing={o.chosen} highlight />}
          {o.chosen?.purchased && <p className="muted">Chosen because you've bought it before, even if another store is cheaper.</p>}
          {o.match === 'SmallerSize' && o.cart !== 'Added' && o.cart !== 'Adding' && (
            <p className="banner warn">Only a smaller pack than you asked for exists, so it was not added automatically.</p>
          )}
          {o.match === 'NotFound' && <p className="muted">No matching in-stock product was found at any store.</p>}
          {o.chosen && canAdd && (
            <button className="primary" disabled={busy} onClick={() => onAdd(item.index)}>
              {o.cart === 'Failed' ? 'Try adding again'
                : o.match === 'SmallerSize' ? 'Add this smaller pack anyway'
                : 'Add to cart'}
            </button>
          )}
          {o.cartError && <p className="banner error">{o.cartError.split('\n')[0]}</p>}
          {o.issues.map((i, k) => <p className="banner warn" key={k}>{i.provider}: {i.message.split('\n')[0]}</p>)}
          {o.candidates.length > 1 && (
            <details>
              <summary>Compare {o.candidates.length} options at this size</summary>
              <div className="candidates">
                {[...o.candidates].sort((a, b) => a.price - b.price).map(c => (
                  <ListingRow
                    key={`${c.provider}${c.productId}`}
                    listing={c}
                    onAdd={canAdd && !(o.chosen && c.provider === o.chosen.provider && c.productId === o.chosen.productId) && !busy
                      ? () => onAdd(item.index, c) : undefined}
                  />
                ))}
              </div>
            </details>
          )}
        </>
      )}
    </article>
  )
}

function ListingRow({ listing, highlight, onAdd }: { listing: Listing; highlight?: boolean; onAdd?: () => void }) {
  return (
    <div className={`listing${highlight ? ' best' : ''}`}>
      <span className={`chip provider-${listing.provider}`}>{listing.provider}</span>
      <span className="title">
        <a href={listing.url} target="_blank" rel="noreferrer">{listing.title}</a>
        {listing.purchased && (
          <span className="badge purchased" title="The store says you bought this before">
            Purchased{listing.purchasedNote ? ` · ${listing.purchasedNote}` : ''}
          </span>
        )}
      </span>
      <span className="size">{formatSize(listing.sizeValue, listing.unit)}</span>
      <span className="price">
        {formatPrice(listing.price)}
        {listing.mrp && listing.mrp > listing.price && <s>{formatPrice(listing.mrp)}</s>}
      </span>
      {onAdd && <button className="link add-this" onClick={onAdd}>Add this</button>}
    </div>
  )
}
