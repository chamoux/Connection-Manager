<script setup lang="ts">
import { useId } from 'vue'
import type { ModeView } from '../../lib/statusText'
import type { ModeSetting } from '../../types'
import AppIcon from '../ui/AppIcon.vue'
import type { IconName } from '../ui/icons'
import InlineError from '../ui/InlineError.vue'
import SegmentedControl from '../ui/SegmentedControl.vue'
import StatusDot from '../ui/StatusDot.vue'

defineProps<{
  title: string
  icon: IconName
  mode: 'game' | 'saver'
  view: ModeView
  setting: ModeSetting
  /** What the selected setting does, in one line. */
  hint: string
  pending: boolean
  error: string | null
}>()
const emit = defineEmits<{ change: [setting: ModeSetting] }>()

const headingId = useId()
const settings = [
  { value: 'auto', label: 'Auto' },
  { value: 'on', label: 'On' },
  { value: 'off', label: 'Off' },
] as const satisfies readonly { value: ModeSetting; label: string }[]
</script>

<template>
  <section class="card flex flex-col gap-3" :aria-labelledby="headingId">
    <header class="flex items-center gap-2.5">
      <span
        class="grid size-8 place-items-center rounded-lg"
        :class="mode === 'game' ? 'bg-game/15 text-game' : 'bg-saver/15 text-saver'"
      >
        <AppIcon :name="icon" />
      </span>
      <h2 :id="headingId" class="text-base font-semibold">{{ title }}</h2>
    </header>

    <div aria-live="polite">
      <p class="flex items-center gap-2 text-lg font-semibold">
        <StatusDot :tone="view.tone" :mode="mode" />
        {{ view.label }}
      </p>
      <p class="mt-0.5 text-ink-muted">{{ view.detail }}</p>
      <p v-if="view.note" class="mt-0.5 text-xs text-ink-faint">{{ view.note }}</p>
    </div>

    <slot />

    <div class="mt-auto flex flex-col gap-1.5 border-t border-line pt-3">
      <SegmentedControl :label="`${title} setting`" :options="settings" :value="setting" :pending="pending" @change="emit('change', $event)" />
      <p class="text-xs text-ink-muted">{{ hint }}</p>
      <InlineError :message="error" />
    </div>
  </section>
</template>
