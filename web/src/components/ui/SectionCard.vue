<script setup lang="ts">
import { useId } from 'vue'

defineProps<{ title: string; description?: string }>()
const headingId = useId()
</script>

<template>
  <section class="card flex flex-col gap-3" :aria-labelledby="headingId">
    <header class="flex items-start justify-between gap-3">
      <div class="min-w-0">
        <h2 :id="headingId" class="text-base font-semibold">{{ title }}</h2>
        <p v-if="description" class="mt-0.5 text-ink-muted">{{ description }}</p>
      </div>
      <div v-if="$slots.actions" class="flex shrink-0 items-center gap-2">
        <slot name="actions" />
      </div>
    </header>
    <slot />
  </section>
</template>
