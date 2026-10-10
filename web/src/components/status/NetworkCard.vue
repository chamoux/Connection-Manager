<script setup lang="ts">
import { computed } from 'vue'
import { networkView } from '../../lib/statusText'
import type { NetworkDto } from '../../types'
import AppIcon from '../ui/AppIcon.vue'
import SectionCard from '../ui/SectionCard.vue'

const props = defineProps<{ network: NetworkDto }>()
const emit = defineEmits<{ manage: [] }>()

const view = computed(() => networkView(props.network))
const icon = computed(() => (props.network.kind === 'wifi' ? 'wifi' : props.network.kind === 'ethernet' ? 'ethernet' : 'offline'))
</script>

<template>
  <SectionCard title="Network">
    <template #actions>
      <button type="button" class="btn-ghost" @click="emit('manage')">
        Data Saver rules
        <AppIcon name="arrow" />
      </button>
    </template>
    <div class="flex items-start gap-3">
      <span class="grid size-9 shrink-0 place-items-center rounded-lg bg-raised text-ink-muted">
        <AppIcon :name="icon" :size="18" />
      </span>
      <div class="min-w-0 flex-1">
        <p class="truncate font-semibold" :title="view.title">{{ view.title }}</p>
        <p class="text-ink-muted">{{ view.kind }}</p>
        <p v-if="view.hotspotNote" class="mt-1 text-ink-muted">Why it counts as your hotspot: {{ view.hotspotNote }}</p>
      </div>
      <ul v-if="view.tags.length" class="flex flex-wrap justify-end gap-1.5" aria-label="Network details">
        <li v-for="tag in view.tags" :key="tag" class="chip">{{ tag }}</li>
      </ul>
    </div>
  </SectionCard>
</template>
