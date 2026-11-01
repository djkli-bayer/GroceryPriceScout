import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, apiKey } from '../api'
import Settings from './Settings'

const mocks = vi.hoisted(() => ({
  health: vi.fn(),
  locations: vi.fn(),
  providers: vi.fn(),
  addLocation: vi.fn(),
  deleteLocation: vi.fn(),
  setDefaultLocation: vi.fn(),
  setProviderEnabled: vi.fn(),
}))

vi.mock('../api', async importOriginal => {
  const actual = await importOriginal<typeof import('../api')>()
  return { ...actual, api: { ...actual.api, ...mocks } }
})

const health = { browser: true, ollama: true, llmEnabled: true, llmModel: 'qwen2.5:7b-instruct', hasDefaultLocation: true }

beforeEach(() => {
  vi.clearAllMocks()
  mocks.health.mockResolvedValue(health)
  mocks.locations.mockResolvedValue([
    { id: 1, label: 'Home', pincode: '560100', isDefault: true },
    { id: 2, label: 'Office', pincode: '560001', isDefault: false },
  ])
  mocks.providers.mockResolvedValue([
    { name: 'Amazon', enabled: true, selectorConfigJson: null },
    { name: 'Blinkit', enabled: false, selectorConfigJson: null },
  ])
  for (const fn of [mocks.addLocation, mocks.deleteLocation, mocks.setDefaultLocation, mocks.setProviderEnabled])
    fn.mockResolvedValue(undefined)
})

describe('Settings', () => {
  it('summarises the system status', async () => {
    render(<Settings />)

    expect(await screen.findByText('Browser connected')).toBeInTheDocument()
    expect(screen.getByText('Smart list reading on (qwen2.5:7b-instruct)')).toBeInTheDocument()
    expect(screen.getByText('Default location set')).toBeInTheDocument()
  })

  it('tells the user how to fix a missing browser or Ollama', async () => {
    mocks.health.mockResolvedValue({ ...health, browser: false, ollama: false, hasDefaultLocation: false })
    render(<Settings />)

    expect(await screen.findByText(/Browser not running/)).toBeInTheDocument()
    expect(screen.getByText(/start-chrome\.ps1/)).toBeInTheDocument()
    expect(screen.getByText(/Ollama not running/)).toBeInTheDocument()
    expect(screen.getByText('No location yet')).toBeInTheDocument()
  })

  it('lists locations and marks the default', async () => {
    render(<Settings />)
    expect(await screen.findByText(/Home · 560100/)).toBeInTheDocument()
    expect(screen.getByText('default')).toBeInTheDocument()
    expect(screen.getByText(/Office · 560001/)).toBeInTheDocument()
  })

  it('switches the default location and removes one', async () => {
    const user = userEvent.setup()
    render(<Settings />)
    await screen.findByText(/Office · 560001/)

    await user.click(screen.getByRole('button', { name: 'Make default' }))
    expect(mocks.setDefaultLocation).toHaveBeenCalledWith(2)

    await user.click(screen.getAllByRole('button', { name: 'Remove' })[1])
    expect(mocks.deleteLocation).toHaveBeenCalledWith(2)
  })

  it('only allows adding a location with a label and a 6-digit pincode', async () => {
    const user = userEvent.setup()
    render(<Settings />)
    await screen.findByText(/Home · 560100/)
    const add = screen.getByRole('button', { name: 'Add' })

    expect(add).toBeDisabled()
    await user.type(screen.getByLabelText('Label'), 'Parents')
    await user.type(screen.getByLabelText('Pincode'), '5600')
    expect(add).toBeDisabled()
    await user.type(screen.getByLabelText('Pincode'), '25')
    expect(add).toBeEnabled()

    await user.click(add)
    expect(mocks.addLocation).toHaveBeenCalledWith('Parents', '560025', false)
    await waitFor(() => expect(screen.getByLabelText('Label')).toHaveValue(''))
  })

  it('toggles stores', async () => {
    const user = userEvent.setup()
    render(<Settings />)

    const blinkit = await screen.findByRole('checkbox', { name: 'Blinkit' })
    expect(blinkit).not.toBeChecked()
    expect(screen.getByRole('checkbox', { name: 'Amazon' })).toBeChecked()

    await user.click(blinkit)
    expect(mocks.setProviderEnabled).toHaveBeenCalledWith('Blinkit', true)
  })

  it('stores the API key locally', async () => {
    const user = userEvent.setup()
    render(<Settings />)

    await user.type(await screen.findByLabelText('API key'), 'abc123')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(apiKey.get()).toBe('abc123')
  })

  it('shows an error when the server cannot be reached', async () => {
    mocks.health.mockRejectedValue(new ApiError(0, 'Cannot reach the server. Is the Grocery Price Scout API running?'))
    render(<Settings />)
    expect(await screen.findByRole('alert')).toHaveTextContent(/Cannot reach the server/)
  })
})
