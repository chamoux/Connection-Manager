<script setup lang="ts">
import AppIcon from '../ui/AppIcon.vue'
import SectionCard from '../ui/SectionCard.vue'

defineProps<{ problems: readonly string[] }>()
</script>

<template>
  <SectionCard title="Problems">
    <p v-if="problems.length === 0" class="flex items-center gap-2 text-ink-muted">
      <AppIcon name="check" class="text-game" />
      Everything is working.
    </p>
    <ul v-else class="flex flex-col gap-2">
      <li v-for="(problem, index) in problems" :key="index" class="flex items-start gap-2.5 rounded-lg border border-warn/30 bg-warn/5 px-3 py-2">
        <AppIcon name="alert" class="mt-0.5 text-warn" />
        <span>{{ problem }}</span>
      </li>
    </ul>
  </SectionCard>
</template>
