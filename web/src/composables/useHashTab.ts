// The page lives in the URL hash (#status, #reports...), so the tray can open the window on a given page.
import { onBeforeUnmount, onMounted, shallowRef } from 'vue'

export const tabs = ['status', 'game-mode', 'data-saver', 'usage', 'reports', 'settings'] as const
export type Tab = (typeof tabs)[number]

const isTab = (value: string): value is Tab => (tabs as readonly string[]).includes(value)
const fromHash = (): Tab => {
  const value = location.hash.replace(/^#\/?/, '')
  return isTab(value) ? value : 'status'
}

export function useHashTab() {
  const current = shallowRef<Tab>(fromHash())
  const sync = () => { current.value = fromHash() }

  onMounted(() => window.addEventListener('hashchange', sync))
  onBeforeUnmount(() => window.removeEventListener('hashchange', sync))

  function go(tab: Tab) {
    current.value = tab
    if (location.hash !== `#${tab}`) location.hash = tab
  }

  return { current, go }
}
