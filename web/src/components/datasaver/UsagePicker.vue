<script setup lang="ts">
// Apps that used data in the last 24 hours, with a tick box each: ticked means allowed on the hotspot.
import { computed, onMounted, shallowRef } from 'vue'
import { api } from '../../api'
import { useActions } from '../../composables/useActions'
import { formatBytes, includesText } from '../../lib/format'
import type { UsageDto } from '../../types'
import ActionButton from '../ui/ActionButton.vue'
import InlineError from '../ui/InlineError.vue'

const props = defineProps<{ allowed: readonly string[]; meterOn: boolean }>()
const actions = useActions()
const usage = shallowRef<UsageDto[] | null>(null)

const rows = computed(() =>
  (usage.value ?? [])
    .map(app => ({ ...app, total: app.bytesIn + app.bytesOut, allowed: includesText(props.allowed, app.path) }))
    .sort((a, b) => b.total - a.total),
)

const load = () => actions.run('load', async () => { usage.value = await api.getUsage() }, { refresh: false })
const toggle = (path: string, allowed: boolean) =>
  actions.run(path, () => (allowed ? api.removeAllowedApp(path) : api.allowApp(path)))

onMounted(load)
</script>

<template>
  <div class="flex flex-col gap-2 rounded-lg border border-line bg-canvas/40 p-3">
    <div class="flex items-center justify-between gap-2">
      <p class="font-medium">Recent apps <span class="font-normal text-ink-muted">(last 24 hours)</span></p>
      <ActionButton variant="btn-ghost" icon="refresh" :pending="actions.isPending('load')" @click="load">Refresh</ActionButton>
    </div>
    <p v-if="!meterOn" class="text-xs text-ink-muted">The traffic meter is off, so this list stays empty. Turn it on in Settings.</p>
    <InlineError :message="actions.errorOf('load')" />

    <p v-if="usage === null && actions.isPending('load')" class="text-ink-muted">Loading…</p>
    <p v-else-if="usage !== null && rows.length === 0" class="text-ink-muted">No app has used the internet yet.</p>
    <ul v-else class="flex max-h-72 flex-col overflow-y-auto" aria-label="Recent apps">
      <li v-for="row in rows" :key="row.path" class="border-b border-line py-1.5 last:border-b-0">
        <label class="flex cursor-pointer items-center gap-3">
          <input
            type="checkbox"
            class="size-4 shrink-0 cursor-pointer accent-accent"
            :checked="row.allowed"
            :aria-busy="actions.isPending(row.path) || undefined"
            @click.prevent="!actions.isPending(row.path) && toggle(row.path, row.allowed)"
          />
          <span class="min-w-0 flex-1">
            <span class="block truncate font-medium" :title="row.path">{{ row.name }}</span>
            <span class="block truncate font-mono text-xs text-ink-muted" :title="row.path">{{ row.path }}</span>
          </span>
          <span v-if="actions.isPending(row.path)" class="spinner text-ink-muted" aria-hidden="true" />
          <span class="shrink-0 text-right tabular-nums text-ink-muted">{{ formatBytes(row.total) }}</span>
        </label>
        <InlineError :message="actions.errorOf(row.path)" class="mt-1 pl-7" />
      </li>
    </ul>
  </div>
</template>
