<script setup lang="ts">
import { computed, shallowRef } from 'vue'
import { formatBytes } from '../../lib/format'
import type { UsageDto } from '../../types'
import AppRowActions from './AppRowActions.vue'

const props = withDefaults(defineProps<{ apps: readonly UsageDto[]; caption: string; allowable?: boolean }>(), { allowable: true })

type SortKey = 'name' | 'bytesIn' | 'bytesOut' | 'total'
const sortKey = shallowRef<SortKey>('total')
const descending = shallowRef(true)

const rows = computed(() => {
  const list = props.apps.map(app => ({ ...app, total: app.bytesIn + app.bytesOut }))
  const direction = descending.value ? -1 : 1
  return list.sort((a, b) => {
    const key = sortKey.value
    const order = key === 'name' ? a.name.localeCompare(b.name) : a[key] - b[key]
    return order * direction
  })
})

function sortBy(key: SortKey) {
  if (sortKey.value === key) descending.value = !descending.value
  else {
    sortKey.value = key
    descending.value = key !== 'name'
  }
}

const ariaSort = (key: SortKey) => (sortKey.value === key ? (descending.value ? 'descending' : 'ascending') : 'none')
const columns: { key: SortKey; label: string; numeric: boolean }[] = [
  { key: 'name', label: 'App', numeric: false },
  { key: 'bytesIn', label: 'In', numeric: true },
  { key: 'bytesOut', label: 'Out', numeric: true },
  { key: 'total', label: 'Total', numeric: true },
]
</script>

<template>
  <div class="overflow-x-auto rounded-lg border border-line">
    <table class="w-full table-fixed border-collapse text-sm">
      <caption class="sr-only">{{ caption }}</caption>
      <colgroup>
        <col />
        <col class="w-20" />
        <col class="w-20" />
        <col class="w-20" />
        <col :class="allowable ? 'w-36' : 'w-24'" />
      </colgroup>
      <thead class="bg-raised/60 text-xs text-ink-muted">
        <tr>
          <th v-for="column in columns" :key="column.key" scope="col" :aria-sort="ariaSort(column.key)" class="py-2 font-semibold" :class="column.numeric ? 'px-2 text-right' : 'px-3 text-left'">
            <button type="button" class="inline-flex cursor-pointer items-center gap-1 rounded hover:text-ink" @click="sortBy(column.key)">
              {{ column.label }}
              <span aria-hidden="true" class="w-2">{{ sortKey === column.key ? (descending ? '↓' : '↑') : '' }}</span>
            </button>
          </th>
          <th scope="col" class="px-2 py-2 text-right font-semibold"><span class="sr-only">Actions</span></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-line">
        <tr v-for="row in rows" :key="row.path" class="align-top hover:bg-raised/40">
          <th scope="row" class="px-3 py-2 text-left font-normal">
            <span class="block truncate font-medium" :title="row.path">{{ row.name }}</span>
            <span class="block truncate font-mono text-xs text-ink-muted" :title="row.path">{{ row.path }}</span>
          </th>
          <td class="px-2 py-2 text-right tabular-nums text-ink-muted">{{ formatBytes(row.bytesIn) }}</td>
          <td class="px-2 py-2 text-right tabular-nums text-ink-muted">{{ formatBytes(row.bytesOut) }}</td>
          <td class="px-2 py-2 text-right font-semibold tabular-nums">{{ formatBytes(row.total) }}</td>
          <td class="px-2 py-1.5"><AppRowActions :path="row.path" :name="row.name" :allowable="allowable" /></td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
