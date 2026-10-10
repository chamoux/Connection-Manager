// Bridge to the tray's settings window (WebView2, Task 16). The page posts { id, cmd, args }; the window forwards
// service commands over the pipe and answers { id, ok, error?, data? }. It also pushes { id: 0, ok: true, data: StatusDto }
// whenever the status changes. No HTML is ever built from these messages: Vue templates only.

import type { BridgeOnlyCommand, StatusDto, UiCommand } from './types'

export interface WebViewMessageEvent {
  data: unknown
}

export interface WebViewLike {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: WebViewMessageEvent) => void): void
  removeEventListener?(type: 'message', listener: (event: WebViewMessageEvent) => void): void
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewLike }
  }
}

/**
 * service: the service answered ok: false; the message is for the user, next to the control.
 * timeout / unavailable: nobody answered; the page shows "Service not running".
 */
export type BridgeErrorKind = 'service' | 'timeout' | 'unavailable'

export class BridgeError extends Error {
  readonly kind: BridgeErrorKind

  constructor(kind: BridgeErrorKind, message: string) {
    super(message)
    this.name = 'BridgeError'
    this.kind = kind
  }
}

export const isConnectionError = (error: unknown) => error instanceof BridgeError && error.kind !== 'service'

export const DEFAULT_TIMEOUT_MS = 10_000
/** A file dialog stays open as long as the user needs; the window always answers, with null when cancelled. */
export const PICKER_TIMEOUT_MS = 10 * 60_000

export type BridgeCommand = UiCommand | BridgeOnlyCommand

export interface CallOptions {
  timeoutMs?: number
}

export interface Bridge {
  call<T = unknown>(cmd: BridgeCommand, args?: unknown, options?: CallOptions): Promise<T>
  /** Returns the unsubscribe function. */
  onStatus(listener: (status: StatusDto) => void): () => void
  /** Stops listening and fails every call still waiting. */
  dispose(): void
}

interface Pending {
  resolve(data: unknown): void
  reject(error: BridgeError): void
  timer: ReturnType<typeof setTimeout>
}

interface Reply {
  id: number
  ok: boolean
  error?: unknown
  data?: unknown
}

const MAX_ID = 0x7fff_ffff // the window reads the id as an Int32

function asReply(raw: unknown): Reply | null {
  let value = raw
  if (typeof value === 'string') {
    try {
      value = JSON.parse(value)
    } catch {
      return null
    }
  }
  if (typeof value !== 'object' || value === null) return null
  const reply = value as Record<string, unknown>
  if (typeof reply.id !== 'number' || !Number.isInteger(reply.id) || typeof reply.ok !== 'boolean') return null
  return reply as unknown as Reply
}

const looksLikeStatus = (data: unknown): data is StatusDto =>
  typeof data === 'object' && data !== null
  && typeof (data as Record<string, unknown>).version === 'number'
  && typeof (data as Record<string, unknown>).settings === 'object'

export function createBridge(view: WebViewLike, defaults: { timeoutMs: number } = { timeoutMs: DEFAULT_TIMEOUT_MS }): Bridge {
  const pending = new Map<number, Pending>()
  const statusListeners = new Set<(status: StatusDto) => void>()
  let nextId = 1
  let disposed = false

  function onMessage(event: WebViewMessageEvent) {
    const reply = asReply(event.data)
    if (!reply) return

    if (reply.id === 0) {
      if (!reply.ok || !looksLikeStatus(reply.data)) return
      for (const listener of [...statusListeners]) {
        try {
          listener(reply.data)
        } catch (error) {
          console.error('status listener failed', error)
        }
      }
      return
    }

    const waiting = pending.get(reply.id)
    if (!waiting) return
    pending.delete(reply.id)
    clearTimeout(waiting.timer)
    if (reply.ok) waiting.resolve(reply.data ?? null)
    else waiting.reject(new BridgeError('service', typeof reply.error === 'string' && reply.error.trim() ? reply.error : 'That did not work.'))
  }

  view.addEventListener('message', onMessage)

  return {
    call<T>(cmd: BridgeCommand, args?: unknown, options?: CallOptions): Promise<T> {
      if (disposed) return Promise.reject(new BridgeError('unavailable', 'The settings window is closing.'))
      const id = nextId
      nextId = nextId >= MAX_ID ? 1 : nextId + 1
      const timeoutMs = options?.timeoutMs ?? defaults.timeoutMs

      return new Promise<T>((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(id)
          reject(new BridgeError('timeout', 'The background service did not answer.'))
        }, timeoutMs)
        pending.set(id, { resolve: data => resolve(data as T), reject, timer })
        try {
          view.postMessage(args === undefined ? { id, cmd } : { id, cmd, args })
        } catch {
          clearTimeout(timer)
          pending.delete(id)
          reject(new BridgeError('unavailable', 'Could not reach the settings window.'))
        }
      })
    },

    onStatus(listener) {
      statusListeners.add(listener)
      return () => statusListeners.delete(listener)
    },

    dispose() {
      disposed = true
      view.removeEventListener?.('message', onMessage)
      statusListeners.clear()
      for (const [id, waiting] of pending) {
        clearTimeout(waiting.timer)
        pending.delete(id)
        waiting.reject(new BridgeError('unavailable', 'The settings window is closing.'))
      }
    },
  }
}

/** Outside the settings window in a production build: every call fails, so the page shows "Service not running". */
const unavailable: Bridge = {
  call: () => Promise.reject(new BridgeError('unavailable', 'Open this page from the Connection Manager tray icon.')),
  onStatus: () => () => {},
  dispose: () => {},
}

let instance: Promise<Bridge> | null = null

async function resolveBridge(): Promise<Bridge> {
  const view = typeof window === 'undefined' ? undefined : window.chrome?.webview
  if (view) return createBridge(view)
  // pnpm dev in a browser: a stand-in host with sample data. import.meta.env.DEV is false in builds,
  // so this branch and the mock module are dropped from dist/.
  if (import.meta.env.DEV) {
    const { createMockHost } = await import('./dev/mockHost')
    return createBridge(createMockHost())
  }
  return unavailable
}

export function getBridge(): Promise<Bridge> {
  instance ??= resolveBridge()
  return instance
}

export async function call<T = unknown>(cmd: BridgeCommand, args?: unknown, options?: CallOptions): Promise<T> {
  return (await getBridge()).call<T>(cmd, args, options)
}

export function onStatus(listener: (status: StatusDto) => void): () => void {
  let off: (() => void) | null = null
  let cancelled = false
  void getBridge().then(bridge => {
    if (!cancelled) off = bridge.onStatus(listener)
  })
  return () => {
    cancelled = true
    off?.()
  }
}
