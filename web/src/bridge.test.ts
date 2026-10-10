import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BridgeError, createBridge, DEFAULT_TIMEOUT_MS, isConnectionError, type WebViewMessageEvent } from './bridge'
import { sampleStatus } from './dev/sampleData'

type Listener = (event: WebViewMessageEvent) => void
type Posted = { id: number; cmd: string; args?: unknown }

/** A stand-in for window.chrome.webview: records what the page posts and lets the test answer as the window. */
function fakeWebView() {
  const listeners = new Set<Listener>()
  const posted: Posted[] = []
  const view = {
    postMessage: vi.fn((message: Posted) => { posted.push(message) }),
    addEventListener: vi.fn((_type: 'message', listener: Listener) => { listeners.add(listener) }),
    removeEventListener: vi.fn((_type: 'message', listener: Listener) => { listeners.delete(listener) }),
  }
  const send = (data: unknown) => listeners.forEach(listener => listener({ data }))
  const idOf = (index: number) => posted[index]?.id ?? -1
  return { view, posted, send, idOf, listeners }
}

beforeEach(() => { vi.useFakeTimers() })
afterEach(() => { vi.useRealTimers() })

describe('requests and replies', () => {
  it('posts { id, cmd, args } and resolves with the data for that id', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const reply = bridge.call('setGameMode', { mode: 'on' })

    expect(host.posted).toEqual([{ id: host.idOf(0), cmd: 'setGameMode', args: { mode: 'on' } }])
    host.send({ id: host.idOf(0), ok: true, data: { done: 1 } })
    await expect(reply).resolves.toEqual({ done: 1 })
  })

  it('leaves args out when there are none, and resolves null without data', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const reply = bridge.call('restoreEverything')

    expect(host.posted).toEqual([{ id: host.idOf(0), cmd: 'restoreEverything' }])
    host.send({ id: host.idOf(0), ok: true })
    await expect(reply).resolves.toBeNull()
  })

  it('matches concurrent replies by id, in any order', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const first = bridge.call('getUsage')
    const second = bridge.call('getReports')
    expect(host.idOf(0)).not.toBe(host.idOf(1))

    host.send({ id: host.idOf(1), ok: true, data: 'reports' })
    host.send({ id: host.idOf(0), ok: true, data: 'usage' })
    await expect(first).resolves.toBe('usage')
    await expect(second).resolves.toBe('reports')
  })

  it('turns ok: false into a service error carrying the message', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const reply = bridge.call('setOptions', {})
    host.send({ id: host.idOf(0), ok: false, error: 'That hotkey is not valid.' })

    const error = await reply.catch((e: unknown) => e)
    expect(error).toBeInstanceOf(BridgeError)
    expect(error).toMatchObject({ kind: 'service', message: 'That hotkey is not valid.' })
    expect(isConnectionError(error)).toBe(false)
  })

  it('gives a plain message when the service sends no error text', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const reply = bridge.call('addGame', { path: 'x' })
    host.send({ id: host.idOf(0), ok: false, error: null })
    await expect(reply).rejects.toMatchObject({ kind: 'service', message: 'That did not work.' })
  })

  it('accepts a reply posted as a JSON string', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const reply = bridge.call('getStatus')
    host.send(JSON.stringify({ id: host.idOf(0), ok: true, data: 42 }))
    await expect(reply).resolves.toBe(42)
  })

  it('ignores replies with unknown ids or a bad shape', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    let settled = false
    const reply = bridge.call('getStatus').finally(() => { settled = true })

    host.send({ id: host.idOf(0) + 100, ok: true, data: 'wrong' })
    host.send({ ok: true, data: 'no id' })
    host.send({ id: String(host.idOf(0)), ok: true })
    host.send({ id: host.idOf(0), data: 'no ok' })
    host.send(null)
    host.send('not json')
    await vi.advanceTimersByTimeAsync(0)
    expect(settled).toBe(false)

    host.send({ id: host.idOf(0), ok: true, data: 'right' })
    await expect(reply).resolves.toBe('right')
  })

  it('answers each id once', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const reply = bridge.call('getStatus')
    host.send({ id: host.idOf(0), ok: true, data: 1 })
    host.send({ id: host.idOf(0), ok: false, error: 'late duplicate' })
    await expect(reply).resolves.toBe(1)
  })

  it('listens to the window once', () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    void bridge.call('getUsage')
    void bridge.call('getReports')
    expect(host.view.addEventListener).toHaveBeenCalledTimes(1)
  })

  it('fails at once when postMessage throws', async () => {
    const host = fakeWebView()
    host.view.postMessage.mockImplementation(() => { throw new Error('gone') })
    const bridge = createBridge(host.view)
    const error = await bridge.call('getStatus').catch((e: unknown) => e)
    expect(error).toMatchObject({ kind: 'unavailable' })
    expect(isConnectionError(error)).toBe(true)
    expect(vi.getTimerCount()).toBe(0)
  })
})

describe('timeouts', () => {
  it('rejects with a timeout after 10 s without a reply', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    let settled = false
    const outcome = bridge.call('getStatus').catch((e: unknown) => e).finally(() => { settled = true })

    await vi.advanceTimersByTimeAsync(DEFAULT_TIMEOUT_MS - 1)
    expect(settled).toBe(false)

    await vi.advanceTimersByTimeAsync(1)
    const error = await outcome
    expect(error).toMatchObject({ kind: 'timeout' })
    expect(isConnectionError(error)).toBe(true)
  })

  it('drops a late reply after the timeout', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const outcome = bridge.call('getStatus').catch((e: unknown) => e)
    await vi.advanceTimersByTimeAsync(DEFAULT_TIMEOUT_MS)
    host.send({ id: host.idOf(0), ok: true, data: 'late' })
    await expect(outcome).resolves.toMatchObject({ kind: 'timeout' })
  })

  it('takes a longer timeout per call, for file pickers', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const reply = bridge.call('pickProgram', undefined, { timeoutMs: 60_000 })
    await vi.advanceTimersByTimeAsync(30_000)
    host.send({ id: host.idOf(0), ok: true, data: { path: 'C:\\x.exe' } })
    await expect(reply).resolves.toEqual({ path: 'C:\\x.exe' })
  })

  it('clears the timer when the reply comes in time', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const reply = bridge.call('getStatus')
    host.send({ id: host.idOf(0), ok: true, data: 'ok' })
    await expect(reply).resolves.toBe('ok')
    expect(vi.getTimerCount()).toBe(0)
  })
})

describe('status pushes', () => {
  it('routes id 0 to the status listeners, not to waiting calls', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const seen: number[] = []
    bridge.onStatus(status => seen.push(status.version))
    let settled = false
    void bridge.call('getStatus').finally(() => { settled = true })

    host.send({ id: 0, ok: true, data: sampleStatus({ version: 7 }) })
    await vi.advanceTimersByTimeAsync(0)
    expect(seen).toEqual([7])
    expect(settled).toBe(false)
  })

  it('ignores pushes that are not a status', () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const listener = vi.fn()
    bridge.onStatus(listener)

    host.send({ id: 0, ok: false, error: 'x' })
    host.send({ id: 0, ok: true, data: null })
    host.send({ id: 0, ok: true, data: { hello: 1 } })
    expect(listener).not.toHaveBeenCalled()
  })

  it('stops after unsubscribe, and one failing listener does not stop the others', () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const broken = vi.fn(() => { throw new Error('boom') })
    const healthy = vi.fn()
    const off = bridge.onStatus(broken)
    bridge.onStatus(healthy)

    host.send({ id: 0, ok: true, data: sampleStatus({ version: 1 }) })
    off()
    host.send({ id: 0, ok: true, data: sampleStatus({ version: 2 }) })
    expect(broken).toHaveBeenCalledTimes(1)
    expect(healthy).toHaveBeenCalledTimes(2)
  })
})

describe('dispose', () => {
  it('fails waiting calls, clears timers and stops listening', async () => {
    const host = fakeWebView()
    const bridge = createBridge(host.view)
    const outcome = bridge.call('getStatus').catch((e: unknown) => e)
    bridge.dispose()

    await expect(outcome).resolves.toMatchObject({ kind: 'unavailable' })
    expect(vi.getTimerCount()).toBe(0)
    expect(host.listeners.size).toBe(0)
    await expect(bridge.call('getStatus')).rejects.toMatchObject({ kind: 'unavailable' })
  })
})
