import { describe, expect, it } from 'vitest'
import { describeItem, formatPrice, formatSize } from './format'
import { parseHash } from './router'

describe('formatSize', () => {
  it.each([
    [200, 'Gram', '200 g'],
    [1000, 'Gram', '1 kg'],
    [1500, 'Gram', '1500 g'],
    [5000, 'Gram', '5 kg'],
    [500, 'Millilitre', '500 ml'],
    [2000, 'Millilitre', '2 l'],
    [12, 'Piece', '12 pcs'],
  ] as const)('%s %s -> %s', (value, unit, expected) => {
    expect(formatSize(value, unit)).toBe(expected)
  })

  it('is empty without a size', () => {
    expect(formatSize(null, null)).toBe('')
  })
})

describe('formatPrice', () => {
  it('trims needless decimals', () => {
    expect(formatPrice(53)).toBe('₹53')
    expect(formatPrice(51.6)).toBe('₹51.6')
    expect(formatPrice(75.456)).toBe('₹75.46')
  })
})

describe('describeItem', () => {
  it('lists name, size, count and brand', () => {
    expect(describeItem({ item: 'turmeric powder', sizeValue: 200, unit: 'Gram', count: 2, brand: 'Tata' }))
      .toBe('turmeric powder 200 g × 2 (Tata)')
  })

  it('omits what is not set', () => {
    expect(describeItem({ item: 'tomatoes', sizeValue: null, unit: null, count: 1, brand: null })).toBe('tomatoes')
  })
})

describe('parseHash', () => {
  it.each([
    ['', { name: 'new' }],
    ['#/', { name: 'new' }],
    ['#/runs', { name: 'runs' }],
    ['#/runs/42', { name: 'run', id: 42 }],
    ['#/settings', { name: 'settings' }],
    ['#/runs/abc', { name: 'new' }],
    ['#/nope', { name: 'new' }],
  ])('%s', (hash, expected) => {
    expect(parseHash(hash)).toEqual(expected)
  })
})
