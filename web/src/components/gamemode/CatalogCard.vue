<script setup lang="ts">
import { api } from '../../api'
import { useActions } from '../../composables/useActions'
import type { CatalogItemDto } from '../../types'
import SectionCard from '../ui/SectionCard.vue'
import ToggleRow from '../ui/ToggleRow.vue'

defineProps<{ catalog: readonly CatalogItemDto[] }>()
const actions = useActions()
const toggle = (id: string) => actions.run(id, () => api.toggleCatalog(id))
</script>

<template>
  <SectionCard
    title="Built-in list"
    description="Known updaters that lose internet while you play. They keep running, so mouse settings and RGB still work."
  >
    <div class="flex flex-col gap-3">
      <ToggleRow
        v-for="item in catalog"
        :key="item.id"
        :label="item.label"
        :checked="item.on"
        :pending="actions.isPending(item.id)"
        :error="actions.errorOf(item.id)"
        @toggle="toggle(item.id)"
      />
    </div>
    <p class="text-xs text-ink-muted">Never blocked: Windows itself, Steam, your games, Discord and anti-cheat.</p>
  </SectionCard>
</template>
