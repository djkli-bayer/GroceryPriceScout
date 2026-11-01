export type SizeUnit = 'Gram' | 'Millilitre' | 'Piece'
export type MatchKind = 'Exact' | 'LargerSize' | 'SmallerSize' | 'NotFound'
export type CartStatus = 'NotAttempted' | 'Added' | 'Failed' | 'SkippedNeedsApproval' | 'SkippedNotFound' | 'Adding'
export type RunStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'

export interface ItemDto {
  item: string
  sizeValue: number | null
  unit: SizeUnit | null
  count: number
  brand: string | null
}

export interface Listing {
  provider: string
  title: string
  brand: string | null
  sizeValue: number
  unit: SizeUnit
  price: number
  mrp: number | null
  inStock: boolean
  url: string
  productId: string
  purchased?: boolean
  purchasedNote?: string | null
}

export interface ProviderIssue {
  provider: string
  message: string
}

export interface StoredOutcome {
  match: MatchKind
  chosen: Listing | null
  candidates: Listing[]
  cart: CartStatus
  cartError: string | null
  issues: ProviderIssue[]
}

export interface RunItem {
  index: number
  item: string
  sizeValue: number | null
  unit: SizeUnit | null
  count: number
  brand: string | null
  status: 'Pending' | 'Done'
  outcome: StoredOutcome | null
}

export interface RunSummary {
  id: number
  createdUtc: string
  pincode: string
  addToCart: boolean
  preferPurchased: boolean
  status: RunStatus
  error: string | null
  itemCount: number
  doneCount: number
}

export interface RunDetail {
  run: RunSummary
  rawText: string
  items: RunItem[]
  events: { createdUtc: string; message: string }[]
}

export interface Location {
  id: number
  label: string
  pincode: string
  isDefault: boolean
}

export interface ProviderSetting {
  name: string
  enabled: boolean
  selectorConfigJson: string | null
}

export interface Health {
  browser: boolean
  ollama: boolean
  llmEnabled: boolean
  llmModel: string | null
  hasDefaultLocation: boolean
}
