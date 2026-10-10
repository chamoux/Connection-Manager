<script setup lang="ts">
// A list of programs, folders or network names, each with a remove button (or other actions through the slot).
import AppIcon from './AppIcon.vue'
import type { IconName } from './icons'
import InlineError from './InlineError.vue'

export interface ListItem {
  key: string
  title: string
  /** Full path or other detail, shown small and truncated (with a tooltip). */
  detail?: string
  icon?: IconName
}

withDefaults(defineProps<{
  items: readonly ListItem[]
  emptyText: string
  label: string
  removeVerb?: string
  isPending?: (key: string) => boolean
  errorOf?: (key: string) => string | null
}>(), { removeVerb: 'Remove', isPending: () => false, errorOf: () => null })

const emit = defineEmits<{ remove: [key: string] }>()
</script>

<template>
  <p v-if="items.length === 0" class="rounded-lg border border-dashed border-line px-3 py-2.5 text-ink-muted">{{ emptyText }}</p>
  <ul v-else :aria-label="label" class="divide-y divide-line overflow-hidden rounded-lg border border-line">
    <li v-for="item in items" :key="item.key" class="bg-canvas/40 px-3 py-2">
      <div class="flex items-center gap-3">
        <AppIcon v-if="item.icon" :name="item.icon" class="text-ink-faint" />
        <div class="min-w-0 flex-1">
          <p class="truncate font-medium" :title="item.title">{{ item.title }}</p>
          <p v-if="item.detail" class="truncate font-mono text-xs text-ink-muted" :title="item.detail">{{ item.detail }}</p>
        </div>
        <slot name="actions" :item="item">
          <button
            type="button"
            class="btn-icon"
            :aria-label="`${removeVerb} ${item.title}`"
            :title="removeVerb"
            :aria-disabled="isPending(item.key) || undefined"
            @click="!isPending(item.key) && emit('remove', item.key)"
          >
            <span v-if="isPending(item.key)" class="spinner" aria-hidden="true" />
            <AppIcon v-else name="x" />
          </button>
        </slot>
      </div>
      <InlineError :message="errorOf(item.key)" class="mt-1" />
    </li>
  </ul>
</template>
