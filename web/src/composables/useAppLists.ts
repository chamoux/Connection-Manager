// "Allow on hotspot" and "Block during games" for one program, as offered by the Usage and Reports pages.
import { api } from '../api'
import { includesText, sameText } from '../lib/format'
import type { Actions } from './useActions'
import { useService } from './useService'

export function useAppLists(actions: Actions) {
  const { status } = useService()

  const isAllowed = (path: string) => includesText(status.value?.settings.allowApps ?? [], path)
  const isBlocked = (path: string) =>
    (status.value?.settings.extraBlocks ?? []).some(block => !block.isFolder && sameText(block.path, path))

  const allow = (key: string, path: string) => actions.run(key, () => api.allowApp(path))
  const block = (key: string, path: string) => actions.run(key, () => api.addBlock(path, false))

  return { isAllowed, isBlocked, allow, block }
}
