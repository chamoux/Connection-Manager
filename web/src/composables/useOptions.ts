// setOptions takes the whole OptionsDto, so a change to one field sends the current options with that field replaced.
import { computed } from 'vue'
import { api } from '../api'
import { firstError, validateOptions } from '../lib/options'
import type { OptionsDto } from '../types'
import type { Actions } from './useActions'
import { useService } from './useService'

export function useOptions(actions: Actions) {
  const { status } = useService()
  const options = computed(() => status.value?.settings.options ?? null)

  /** Checks the changed fields first; the error shows under `key`. True when the service accepted it. */
  async function save(key: string, patch: Partial<OptionsDto>): Promise<boolean> {
    const current = options.value
    if (!current) return false
    const next: OptionsDto = { ...current, ...patch }
    if (typeof next.hotkey === 'string') next.hotkey = next.hotkey.trim()

    const problem = firstError(validateOptions(next), Object.keys(patch) as (keyof OptionsDto)[])
    if (problem) {
      actions.setError(key, problem)
      return false
    }
    return actions.run(key, () => api.setOptions(next))
  }

  return { options, save }
}
