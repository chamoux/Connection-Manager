import { onScopeDispose, shallowRef, watch, type Ref } from 'vue'

/** The current time, ticking every second while `active` is true (a countdown is on screen), and idle otherwise. */
export function useNow(active: Ref<boolean>) {
  const now = shallowRef(Date.now())
  let timer: ReturnType<typeof setInterval> | null = null

  const stop = () => {
    if (timer) clearInterval(timer)
    timer = null
  }

  watch(active, on => {
    stop()
    now.value = Date.now()
    if (on) timer = setInterval(() => { now.value = Date.now() }, 1000)
  }, { immediate: true })

  onScopeDispose(stop)
  return now
}
