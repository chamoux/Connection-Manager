<script setup lang="ts">
import type { Tab } from '../../composables/useHashTab'
import type { ModeView } from '../../lib/statusText'
import AppIcon from '../ui/AppIcon.vue'
import type { IconName } from '../ui/icons'
import StatusDot from '../ui/StatusDot.vue'

defineProps<{
  current: Tab
  game: ModeView | null
  saver: ModeView | null
  problemCount: number
  hasNewReport: boolean
}>()
const emit = defineEmits<{ select: [tab: Tab] }>()

const items: { tab: Tab; label: string; icon: IconName }[] = [
  { tab: 'status', label: 'Status', icon: 'status' },
  { tab: 'game-mode', label: 'Game Mode', icon: 'game' },
  { tab: 'data-saver', label: 'Data Saver', icon: 'saver' },
  { tab: 'usage', label: 'Usage', icon: 'usage' },
  { tab: 'reports', label: 'Reports', icon: 'reports' },
  { tab: 'settings', label: 'Settings', icon: 'settings' },
]
</script>

<template>
  <aside class="flex w-48 shrink-0 flex-col border-r border-line bg-surface">
    <div class="flex items-center gap-2.5 px-4 pt-4 pb-5">
      <span class="grid size-7 place-items-center rounded-lg bg-accent/15 text-accent">
        <AppIcon name="shield" :size="16" />
      </span>
      <span class="font-display text-[0.9375rem] leading-tight font-semibold">Connection<br />Manager</span>
    </div>

    <nav aria-label="Sections" class="flex flex-col gap-0.5 px-2">
      <button
        v-for="item in items"
        :key="item.tab"
        type="button"
        class="group flex h-9 cursor-pointer items-center gap-2.5 rounded-md px-2.5 text-left font-medium transition-colors duration-150"
        :class="current === item.tab ? 'bg-raised text-ink' : 'text-ink-muted hover:bg-raised/60 hover:text-ink'"
        :aria-current="current === item.tab ? 'page' : undefined"
        @click="emit('select', item.tab)"
      >
        <span class="h-4 w-0.5 rounded-full" :class="current === item.tab ? 'bg-accent' : 'bg-transparent'" aria-hidden="true" />
        <AppIcon :name="item.icon" :class="current === item.tab ? 'text-accent' : ''" />
        <span class="flex-1">{{ item.label }}</span>
        <span
          v-if="item.tab === 'status' && problemCount > 0"
          class="rounded-full bg-warn/15 px-1.5 text-xs font-semibold text-warn"
          :aria-label="`${problemCount} problem${problemCount === 1 ? '' : 's'}`"
        >{{ problemCount }}</span>
        <span v-if="item.tab === 'reports' && hasNewReport" class="size-2 rounded-full bg-accent" role="img" aria-label="New report" />
      </button>
    </nav>

    <dl v-if="game && saver" class="mt-auto flex flex-col gap-2.5 border-t border-line px-4 py-3 text-xs">
      <div v-for="row in [{ name: 'Game Mode', view: game, mode: 'game' as const }, { name: 'Data Saver', view: saver, mode: 'saver' as const }]" :key="row.name" class="flex items-start gap-2.5">
        <StatusDot :tone="row.view.tone" :mode="row.mode" class="mt-1" />
        <div class="min-w-0">
          <dt class="text-ink-muted">{{ row.name }}</dt>
          <dd class="truncate font-medium" :title="row.view.label">{{ row.view.label }}</dd>
        </div>
      </div>
    </dl>
  </aside>
</template>
