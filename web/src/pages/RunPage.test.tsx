import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api'
import { detail, listing, outcome, runItem } from '../test/factories'
import RunPage from './RunPage'

const mocks = vi.hoisted(() => ({
  run: vi.fn(),
  cancelRun: vi.fn(),
  deleteRun: vi.fn(),
  createRun: vi.fn(),
  addToCart: vi.fn(),
}))

vi.mock('../api', async importOriginal => {
  const actual = await importOriginal<typeof import('../api')>()
  return { ...actual, api: { ...actual.api, ...mocks } }
})

beforeEach(() => {
  vi.clearAllMocks()
  mocks.addToCart.mockResolvedValue(undefined)
  mocks.createRun.mockResolvedValue({ id: 2 })
})

afterEach(() => {
  vi.useRealTimers()
})

const smaller = listing({ title: 'Everest Turmeric 100 g', sizeValue: 100, price: 40, productId: 'small' })

describe('RunPage results', () => {
  it('shows the chosen product, size, price and the estimated total', async () => {
    mocks.run.mockResolvedValue(detail([runItem({ count: 2 })]))
    render(<RunPage id={1} />)

    expect(await screen.findByText('turmeric powder 200 g × 2')).toBeInTheDocument()
    expect(screen.getByText('Exact size')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Hamdard Turmeric Powder' })).toHaveAttribute('href', 'https://blinkit.com/prn/product/prid/1')
    expect(screen.getByText('Estimated total')).toBeInTheDocument()
    expect(screen.getByText('₹106')).toBeInTheDocument() // 2 x 53
  })

  it('marks products the store says were bought before, and explains the choice', async () => {
    const bought = listing({ provider: 'Amazon', title: 'Everest Turmeric', price: 80, purchased: true, purchasedNote: 'Jan 2025', productId: 'B1' })
    mocks.run.mockResolvedValue(detail([runItem({ outcome: outcome({ chosen: bought }) })]))
    render(<RunPage id={1} />)

    expect(await screen.findByText('Purchased · Jan 2025')).toBeInTheDocument()
    expect(screen.getByText(/bought it before, even if another store is cheaper/i)).toBeInTheDocument()
  })

  it('does not claim a purchase for ordinary products', async () => {
    mocks.run.mockResolvedValue(detail([runItem()]))
    render(<RunPage id={1} />)
    await screen.findByText('Exact size')
    expect(screen.queryByText(/Purchased/)).not.toBeInTheDocument()
  })

  it('shows waiting items, run errors and provider warnings', async () => {
    mocks.run.mockResolvedValue(detail([
      runItem({ index: 0, outcome: outcome({ issues: [{ provider: 'Blinkit', message: 'No products were shown\nstack' }] }) }),
      runItem({ index: 1, item: 'milk', status: 'Pending', outcome: null }),
    ], { status: 'Failed', error: 'Could not connect to the browser.' }))
    render(<RunPage id={1} />)

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not connect to the browser.')
    expect(screen.getByText('Blinkit: No products were shown')).toBeInTheDocument()
    expect(screen.getByText('Waiting…')).toBeInTheDocument()
  })

  it('reports a missing run', async () => {
    mocks.run.mockRejectedValue(new ApiError(404, 'Not Found'))
    render(<RunPage id={99} />)
    expect(await screen.findByText('Not Found')).toBeInTheDocument()
  })
})

describe('RunPage add to cart', () => {
  it('offers the flagged smaller pack for approval and adds it on request', async () => {
    mocks.run.mockResolvedValue(detail([runItem({
      outcome: outcome({ match: 'SmallerSize', chosen: smaller, cart: 'SkippedNeedsApproval' }),
    })]))
    const user = userEvent.setup()
    render(<RunPage id={1} />)

    expect(await screen.findByText(/not added automatically/i)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Add this smaller pack anyway' }))

    expect(mocks.addToCart).toHaveBeenCalledWith(1, 0, undefined) // the app's own pick
  })

  it('offers a plain add button for a price-check result', async () => {
    mocks.run.mockResolvedValue(detail([runItem()]))
    const user = userEvent.setup()
    render(<RunPage id={1} />)

    await user.click(await screen.findByRole('button', { name: 'Add to cart' }))
    expect(mocks.addToCart).toHaveBeenCalledWith(1, 0, undefined)
  })

  it.each(['Added', 'Adding'] as const)('hides every add button once the item is %s', async cart => {
    const other = listing({ provider: 'BigBasket', productId: '2' })
    mocks.run.mockResolvedValue(detail([runItem({ outcome: outcome({ cart, candidates: [listing(), other] }) })]))
    render(<RunPage id={1} />)

    await screen.findByText(cart === 'Added' ? 'Added to cart' : 'Adding to cart…')
    expect(screen.queryByRole('button', { name: /add/i })).not.toBeInTheDocument()
  })

  it('lets a failed addition be retried and shows why it failed', async () => {
    mocks.run.mockResolvedValue(detail([runItem({ outcome: outcome({ cart: 'Failed', cartError: 'Could not connect to the browser.\nmore' }) })]))
    const user = userEvent.setup()
    render(<RunPage id={1} />)

    expect(await screen.findByText('Could not connect to the browser.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Try adding again' }))
    expect(mocks.addToCart).toHaveBeenCalledTimes(1)
  })

  it('adds a different option picked from the comparison list', async () => {
    const chosen = listing({ provider: 'BigBasket', title: 'Catch Turmeric', price: 51.6, productId: 'a' })
    const other = listing({ provider: 'Amazon', title: 'Everest Turmeric', price: 80, productId: 'b' })
    mocks.run.mockResolvedValue(detail([runItem({ outcome: outcome({ chosen, candidates: [chosen, other] }) })]))
    const user = userEvent.setup()
    render(<RunPage id={1} />)

    await user.click(await screen.findByText('Compare 2 options at this size'))
    const rows = screen.getAllByText(/Turmeric/).map(e => e.closest('.listing')).filter(Boolean) as HTMLElement[]
    const otherRow = rows.find(r => within(r).queryByText('Everest Turmeric'))!
    await user.click(within(otherRow).getByRole('button', { name: 'Add this' }))

    expect(mocks.addToCart).toHaveBeenCalledWith(1, 0, { provider: 'Amazon', productId: 'b' })
    // the app's own pick has no "Add this" button: it already has the main button
    const chosenRow = rows.find(r => within(r).queryByText('Catch Turmeric') && r.classList.contains('best'))!
    expect(within(chosenRow).queryByRole('button', { name: 'Add this' })).not.toBeInTheDocument()
  })

  it('shows the server message when adding is refused', async () => {
    mocks.run.mockResolvedValue(detail([runItem()]))
    mocks.addToCart.mockRejectedValue(new ApiError(409, 'This item was already added.'))
    const user = userEvent.setup()
    render(<RunPage id={1} />)

    await user.click(await screen.findByRole('button', { name: 'Add to cart' }))
    expect(await screen.findByText('This item was already added.')).toBeInTheDocument()
  })
})

describe('RunPage polling and actions', () => {
  it('keeps polling while an item is being added and shows the final result', async () => {
    vi.useFakeTimers()
    mocks.run
      .mockResolvedValueOnce(detail([runItem({ outcome: outcome({ cart: 'Adding' }) })]))
      .mockResolvedValue(detail([runItem({ outcome: outcome({ cart: 'Added' }) })]))
    render(<RunPage id={1} />)

    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(screen.getByText('Adding to cart…')).toBeInTheDocument()

    await act(async () => { await vi.advanceTimersByTimeAsync(3100) })
    expect(screen.getByText('Added to cart')).toBeInTheDocument()

    const callsAfterDone = mocks.run.mock.calls.length
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000) })
    expect(mocks.run.mock.calls.length).toBe(callsAfterDone) // polling stops once nothing is in flight
  })

  it('shows progress and a cancel button for a running run', async () => {
    mocks.run.mockResolvedValue(detail([
      runItem({ index: 0 }),
      runItem({ index: 1, status: 'Pending', outcome: null }),
    ], { status: 'Running' }))
    mocks.cancelRun.mockResolvedValue(undefined)
    const user = userEvent.setup()
    render(<RunPage id={1} />)

    expect(await screen.findByRole('progressbar')).toHaveAttribute('aria-valuenow', '50')
    expect(screen.getByText('1 of 2 items done')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Cancel run' }))
    expect(mocks.cancelRun).toHaveBeenCalledWith(1)
  })

  it('re-runs a finished run as a price check with the same choices', async () => {
    mocks.run.mockResolvedValue(detail([runItem({ count: 2, brand: 'Tata' })], { preferPurchased: false }))
    const user = userEvent.setup()
    render(<RunPage id={1} />)

    await user.click(await screen.findByRole('button', { name: 'Check prices again' }))

    expect(mocks.createRun).toHaveBeenCalledWith(
      [{ item: 'turmeric powder', sizeValue: 200, unit: 'Gram', count: 2, brand: 'Tata' }],
      '560100', false, 'turmeric powder 200g', false)
  })
})
