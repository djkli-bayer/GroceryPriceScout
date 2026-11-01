import type { CartStatus, ItemDto, MatchKind, SizeUnit } from './types'

const num = (n: number) => String(Number(n.toFixed(2)))

export function formatSize(value: number | null, unit: SizeUnit | null): string {
  if (value === null) return ''
  switch (unit) {
    case 'Gram':
      return value >= 1000 && value % 1000 === 0 ? `${num(value / 1000)} kg` : `${num(value)} g`
    case 'Millilitre':
      return value >= 1000 && value % 1000 === 0 ? `${num(value / 1000)} l` : `${num(value)} ml`
    default:
      return `${num(value)} pcs`
  }
}

export function formatPrice(price: number): string {
  return `₹${num(price)}`
}

export function formatDate(iso: string): string {
  const d = new Date(iso)
  return d.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
}

export function describeItem(i: Pick<ItemDto, 'item' | 'sizeValue' | 'unit' | 'count' | 'brand'>): string {
  const parts = [i.item]
  if (i.sizeValue !== null) parts.push(formatSize(i.sizeValue, i.unit))
  if (i.count > 1) parts.push(`× ${i.count}`)
  if (i.brand) parts.push(`(${i.brand})`)
  return parts.join(' ')
}

export const matchLabel: Record<MatchKind, string> = {
  Exact: 'Exact size',
  LargerSize: 'Next larger size',
  SmallerSize: 'Only a smaller size',
  NotFound: 'Not found',
}

export const cartLabel: Record<CartStatus, string> = {
  NotAttempted: 'Not added (price check)',
  Added: 'Added to cart',
  Failed: 'Could not add',
  SkippedNeedsApproval: 'Not added: needs your OK',
  SkippedNotFound: 'Nothing to add',
  Adding: 'Adding to cart…',
}

export const UNIT_OPTIONS: { value: SizeUnit; label: string }[] = [
  { value: 'Gram', label: 'g' },
  { value: 'Millilitre', label: 'ml' },
  { value: 'Piece', label: 'pcs' },
]
