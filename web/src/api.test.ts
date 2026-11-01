import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, apiKey, ApiError } from './api'

const fetchMock = vi.fn()

function respond(status: number, body?: unknown) {
  fetchMock.mockResolvedValueOnce(new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
  }))
}

beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('api client', () => {
  it('sends JSON bodies and parses JSON responses', async () => {
    respond(202, { id: 7 })

    const res = await api.createRun([{ item: 'rice', sizeValue: 1000, unit: 'Gram', count: 1, brand: null }], '560100', true, 'rice 1kg', false)

    expect(res).toEqual({ id: 7 })
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/runs')
    expect(init.method).toBe('POST')
    expect(init.headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(init.body)).toMatchObject({ pincode: '560100', addToCart: true, preferPurchased: false, text: 'rice 1kg' })
  })

  it('treats empty responses as undefined', async () => {
    respond(204)
    await expect(api.deleteRun(3)).resolves.toBeUndefined()
  })

  it('adds the API key header when one is stored', async () => {
    apiKey.set('s3cret')
    respond(200, [])
    await api.runs()
    expect(fetchMock.mock.calls[0][1].headers['X-Api-Key']).toBe('s3cret')
  })

  it('omits the header when no key is stored', async () => {
    respond(200, [])
    await api.runs()
    expect(fetchMock.mock.calls[0][1].headers['X-Api-Key']).toBeUndefined()
  })

  it('surfaces the server error message', async () => {
    respond(400, { error: 'A 6-digit pincode is required.' })
    await expect(api.createRun([], null, false)).rejects.toMatchObject({ status: 400, message: 'A 6-digit pincode is required.' })
  })

  it('explains a 401', async () => {
    respond(401, { error: 'Missing or invalid X-Api-Key.' })
    const err = await api.runs().catch(e => e)
    expect(err).toBeInstanceOf(ApiError)
    expect(err.status).toBe(401)
    expect(err.message).toMatch(/API key/)
  })

  it('reports an unreachable server', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    await expect(api.health()).rejects.toMatchObject({ status: 0, message: expect.stringMatching(/Cannot reach the server/) })
  })

  it('falls back to the status text when the error body is not JSON', async () => {
    fetchMock.mockResolvedValueOnce(new Response('oops', { status: 500, statusText: 'Internal Server Error' }))
    await expect(api.runs()).rejects.toMatchObject({ status: 500, message: 'Internal Server Error' })
  })

  it('targets the add-to-cart endpoint for a chosen option', async () => {
    respond(202)
    await api.addToCart(5, 2, { provider: 'BigBasket', productId: 'x1' })
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/runs/5/items/2/add-to-cart')
    expect(JSON.parse(init.body)).toEqual({ provider: 'BigBasket', productId: 'x1' })
  })

  it('encodes provider names in paths', async () => {
    respond(204)
    await api.setProviderEnabled('Big Basket', false)
    expect(fetchMock.mock.calls[0][0]).toBe('/api/providers/Big%20Basket/enabled')
  })
})
