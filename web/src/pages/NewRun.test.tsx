import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api'
import NewRun from './NewRun'

const mocks = vi.hoisted(() => ({
  locations: vi.fn(),
  parseList: vi.fn(),
  createRun: vi.fn(),
}))

vi.mock('../api', async importOriginal => {
  const actual = await importOriginal<typeof import('../api')>()
  return { ...actual, api: { ...actual.api, ...mocks } }
})

const parsed = {
  items: [
    { item: 'turmeric powder', sizeValue: 200, unit: 'Gram', count: 2, brand: 'Tata' },
    { item: 'milk', sizeValue: 500, unit: 'Millilitre', count: 1, brand: null },
  ],
}

async function readList(text = 'turmeric powder 200g x2\nmilk 500ml') {
  const user = userEvent.setup()
  render(<NewRun />)
  await user.type(screen.getByLabelText('Grocery list'), text.replace(/\n/g, '{Enter}'))
  await user.click(screen.getByRole('button', { name: 'Read list' }))
  await screen.findByText(/Check what I understood/)
  return user
}

beforeEach(() => {
  vi.clearAllMocks()
  mocks.locations.mockResolvedValue([{ id: 1, label: 'Home', pincode: '560100', isDefault: true }])
  mocks.parseList.mockResolvedValue(parsed)
  mocks.createRun.mockResolvedValue({ id: 9 })
})

describe('NewRun', () => {
  it('disables reading until there is text', () => {
    render(<NewRun />)
    expect(screen.getByRole('button', { name: 'Read list' })).toBeDisabled()
  })

  it('reads the list and shows one editable row per item', async () => {
    await readList()

    expect(mocks.parseList).toHaveBeenCalledWith('turmeric powder 200g x2\nmilk 500ml')
    const names = screen.getAllByLabelText('Item') as HTMLInputElement[]
    expect(names.map(n => n.value)).toEqual(['turmeric powder', 'milk'])
    expect((screen.getAllByLabelText('Size') as HTMLInputElement[]).map(n => n.value)).toEqual(['200', '500'])
    expect((screen.getAllByLabelText('Quantity') as HTMLInputElement[]).map(n => n.value)).toEqual(['2', '1'])
    expect((screen.getAllByLabelText('Brand') as HTMLInputElement[])[0].value).toBe('Tata')
  })

  it('preselects the default location', async () => {
    await readList()
    expect(await screen.findByRole('combobox', { name: 'Deliver to' })).toHaveValue('560100')
  })

  it('starts a price check with the reviewed items and opens the run', async () => {
    const user = await readList()

    await user.click(await screen.findByRole('button', { name: 'Find cheapest prices' }))

    await waitFor(() => expect(mocks.createRun).toHaveBeenCalledTimes(1))
    const [items, pincode, addToCart, text, preferPurchased] = mocks.createRun.mock.calls[0]
    expect(items).toEqual(parsed.items)
    expect(pincode).toBe('560100')
    expect(addToCart).toBe(false) // never adds to carts unless asked
    expect(text).toBe('turmeric powder 200g x2\nmilk 500ml')
    expect(preferPurchased).toBe(true)
    await waitFor(() => expect(window.location.hash).toBe('#/runs/9'))
  })

  it('sends edits made in the review table', async () => {
    const user = await readList()

    const names = screen.getAllByLabelText('Item')
    await user.clear(names[1])
    await user.type(names[1], 'toned milk')
    await user.clear(screen.getAllByLabelText('Quantity')[1])
    await user.type(screen.getAllByLabelText('Quantity')[1], '3')
    await user.click(screen.getAllByRole('button', { name: 'Remove item' })[0])
    await user.click(await screen.findByRole('button', { name: 'Find cheapest prices' }))

    const items = mocks.createRun.mock.calls[0][0]
    expect(items).toEqual([{ item: 'toned milk', sizeValue: 500, unit: 'Millilitre', count: 3, brand: null }])
  })

  it('makes the cart option explicit and passes it on', async () => {
    const user = await readList()

    expect(screen.getByText(/nothing is added/i)).toBeInTheDocument()
    await user.click(screen.getByRole('checkbox', { name: /add the cheapest item/i }))
    expect(screen.getByText(/added to your real carts/i)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Find cheapest and add to carts' }))
    expect(mocks.createRun.mock.calls[0][2]).toBe(true)
  })

  it('can switch off the preference for previously purchased products', async () => {
    const user = await readList()

    const box = screen.getByRole('checkbox', { name: /bought before/i })
    expect(box).toBeChecked()
    await user.click(box)
    await user.click(screen.getByRole('button', { name: 'Find cheapest prices' }))

    expect(mocks.createRun.mock.calls[0][4]).toBe(false)
  })

  it('asks for a pincode when no location is saved', async () => {
    mocks.locations.mockResolvedValue([])
    const user = await readList()

    const start = screen.getByRole('button', { name: 'Find cheapest prices' })
    expect(start).toBeDisabled()
    await user.type(screen.getByPlaceholderText('6-digit pincode'), '56a0100')
    expect(screen.getByPlaceholderText('6-digit pincode')).toHaveValue('560100') // non-digits are dropped
    expect(start).toBeEnabled()
  })

  it('shows the server error when reading fails', async () => {
    mocks.parseList.mockRejectedValue(new ApiError(400, 'List is too long.'))
    const user = userEvent.setup()
    render(<NewRun />)

    await user.type(screen.getByLabelText('Grocery list'), 'rice')
    await user.click(screen.getByRole('button', { name: 'Read list' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('List is too long.')
  })

  it('shows the server error when starting fails and stays on the page', async () => {
    mocks.createRun.mockRejectedValue(new ApiError(400, "'bananas' was read as 12 packs."))
    const user = await readList()

    await user.click(await screen.findByRole('button', { name: 'Find cheapest prices' }))

    expect(await screen.findByRole('alert')).toHaveTextContent("'bananas' was read as 12 packs.")
    expect(window.location.hash).not.toContain('runs')
    expect(screen.getByRole('button', { name: 'Find cheapest prices' })).toBeEnabled()
  })

  it('hides the review table again when the text is edited', async () => {
    const user = await readList()
    await user.type(screen.getByLabelText('Grocery list'), ' more')
    expect(screen.queryByText(/Check what I understood/)).not.toBeInTheDocument()
  })
})
