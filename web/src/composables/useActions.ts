// Runs bridge calls for one card. Each control has a key, so its busy state and its error show next to it.
import { reactive } from 'vue'
import { BridgeError, isConnectionError } from '../bridge'
import { useService } from './useService'

export interface RunOptions {
  /** Fetch the status after success (default). Reads such as getUsage pass false. */
  refresh?: boolean
}

export function messageOf(error: unknown): string {
  if (error instanceof BridgeError) {
    if (error.kind === 'service') return error.message
    if (error.kind === 'timeout') return 'The background service did not answer. Try again in a moment.'
    return 'Could not reach the background service.'
  }
  return 'Something went wrong. Try again.'
}

export function useActions() {
  const { refresh, markDisconnected } = useService()
  const pending = reactive(new Set<string>())
  const errors = reactive(new Map<string, string>())

  /** True when the call worked. Errors are kept under the key until the next attempt. */
  async function run(key: string, task: () => Promise<unknown>, options: RunOptions = {}): Promise<boolean> {
    if (pending.has(key)) return false
    pending.add(key)
    errors.delete(key)
    try {
      await task()
      if (options.refresh !== false) await refresh()
      return true
    } catch (error) {
      errors.set(key, messageOf(error))
      if (isConnectionError(error)) markDisconnected()
      return false
    } finally {
      pending.delete(key)
    }
  }

  return {
    run,
    isPending: (key: string) => pending.has(key),
    errorOf: (key: string) => errors.get(key) ?? null,
    setError: (key: string, message: string) => errors.set(key, message),
    clearError: (key: string) => errors.delete(key),
  }
}

export type Actions = ReturnType<typeof useActions>
