import type { Listing, RunDetail, RunItem, RunSummary, StoredOutcome } from '../types'

export const listing = (over: Partial<Listing> = {}): Listing => ({
  provider: 'Blinkit',
  title: 'Hamdard Turmeric Powder',
  brand: null,
  sizeValue: 200,
  unit: 'Gram',
  price: 53,
  mrp: 100,
  inStock: true,
  url: 'https://blinkit.com/prn/product/prid/1',
  productId: '1',
  ...over,
})

export const outcome = (over: Partial<StoredOutcome> = {}): StoredOutcome => {
  const chosen = over.chosen === undefined ? listing() : over.chosen
  return {
    match: 'Exact',
    chosen,
    candidates: chosen ? [chosen] : [],
    cart: 'NotAttempted',
    cartError: null,
    issues: [],
    ...over,
  }
}

export const runItem = (over: Partial<RunItem> = {}): RunItem => ({
  index: 0,
  item: 'turmeric powder',
  sizeValue: 200,
  unit: 'Gram',
  count: 1,
  brand: null,
  status: 'Done',
  outcome: outcome(),
  ...over,
})

export const summary = (over: Partial<RunSummary> = {}): RunSummary => ({
  id: 1,
  createdUtc: '2026-11-01T08:00:00Z',
  pincode: '560100',
  addToCart: false,
  preferPurchased: true,
  status: 'Completed',
  error: null,
  itemCount: 1,
  doneCount: 1,
  ...over,
})

export const detail = (items: RunItem[], over: Partial<RunSummary> = {}): RunDetail => ({
  run: summary({ itemCount: items.length, doneCount: items.filter(i => i.outcome).length, ...over }),
  rawText: 'turmeric powder 200g',
  items,
  events: [],
})
