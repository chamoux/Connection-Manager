<script setup lang="ts">
// The same colors as the tray icon: green (violet for Data Saver) when working, grey when waiting, red when off.
// Always paired with a text label, so color is never the only signal.
import { computed } from 'vue'
import type { Tone } from '../../lib/statusText'

const props = defineProps<{ tone: Tone; mode: 'game' | 'saver' }>()

const classes = computed(() => {
  if (props.tone === 'off') return 'bg-danger'
  if (props.tone === 'idle') return 'bg-ink-faint'
  return props.mode === 'game' ? 'bg-game ring-4 ring-game/20' : 'bg-saver ring-4 ring-saver/20'
})
</script>

<template>
  <span class="inline-block size-2.5 shrink-0 rounded-full" :class="classes" aria-hidden="true" />
</template>
