// The one copy of the service's status, shared by every page. It comes from getStatus on load and from pushes after that.
import { computed, shallowRef } from 'vue'
import { api } from '../api'
import { onStatus } from '../bridge'
import type { StatusDto } from '../types'

export type Connection = 'loading' | 'connected' | 'disconnected'

const RETRY_MS = 5000

const status = shallowRef<StatusDto | null>(null)
const connection = shallowRef<Connection>('loading')
let started = false
let retryTimer: ReturnType<typeof setTimeout> | null = null

function apply(next: StatusDto, pushed: boolean) {
  const current = status.value
  // Pushes are newest-only snapshots, in order. A getStatus reply can be overtaken by a push, so an older one is dropped,
  // unless the service may have restarted (its version starts again from the beginning).
  if (!pushed && current && connection.value === 'connected' && next.version < current.version) return
  status.value = next
  connection.value = 'connected'
}

function scheduleRetry() {
  if (retryTimer) return
  retryTimer = setTimeout(() => {
    retryTimer = null
    void refresh()
  }, RETRY_MS)
}

/** Nobody answered: show the banner and keep trying in the background. */
function markDisconnected() {
  connection.value = 'disconnected'
  scheduleRetry()
}

async function refresh(): Promise<void> {
  try {
    apply(await api.getStatus(), false)
  } catch {
    markDisconnected()
  }
}

export function startService() {
  if (started) return
  started = true
  onStatus(next => apply(next, true))
  void refresh()
}

// Read-only views: only this module writes the status.
const statusView = computed(() => status.value)
const connectionView = computed(() => connection.value)

export function useService() {
  return { status: statusView, connection: connectionView, refresh, markDisconnected }
}
