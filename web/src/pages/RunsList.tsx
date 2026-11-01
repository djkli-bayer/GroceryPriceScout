import { useEffect, useState } from 'react'
import { api, ApiError } from '../api'
import { formatDate } from '../format'
import type { RunSummary } from '../types'

export default function RunsList() {
  const [runs, setRuns] = useState<RunSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.runs().then(setRuns).catch(e => setError(e instanceof ApiError ? e.message : 'Could not load runs.'))
  }, [])

  return (
    <section>
      <h1>Past runs</h1>
      {error && <p className="banner error">{error}</p>}
      {runs === null && !error && <p className="muted">Loading…</p>}
      {runs?.length === 0 && <p className="muted">No runs yet. Start one from "New".</p>}
      <div className="results">
        {runs?.map(r => (
          <a className="card run-link" key={r.id} href={`#/runs/${r.id}`}>
            <span><strong>Run #{r.id}</strong> <span className={`badge status-${r.status}`}>{r.status}</span></span>
            <span className="muted">{formatDate(r.createdUtc)} · {r.doneCount}/{r.itemCount} items · {r.addToCart ? 'added to carts' : 'price check'}</span>
          </a>
        ))}
      </div>
    </section>
  )
}
