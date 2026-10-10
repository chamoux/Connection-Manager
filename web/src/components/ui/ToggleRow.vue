<script setup lang="ts">
// A labelled on/off switch. It shows the service's value only: clicking asks for a change, and the switch moves
// when the new status arrives, so a refused change never leaves it in the wrong position.
import { computed, useId } from 'vue'
import InlineError from './InlineError.vue'

const props = defineProps<{
  label: string
  description?: string
  checked: boolean
  pending?: boolean
  disabled?: boolean
  error?: string | null
}>()
const emit = defineEmits<{ toggle: [] }>()

const id = useId()
const describedBy = computed(() => [props.description ? `${id}-desc` : '', props.error ? `${id}-err` : ''].filter(Boolean).join(' ') || undefined)
</script>

<template>
  <div class="flex flex-col gap-1">
    <div class="flex items-start justify-between gap-4">
      <div class="min-w-0">
        <label :for="id" class="cursor-pointer font-medium text-ink">{{ label }}</label>
        <p v-if="description" :id="`${id}-desc`" class="text-ink-muted">{{ description }}</p>
      </div>
      <button
        :id="id"
        type="button"
        role="switch"
        :aria-checked="checked"
        :aria-describedby="describedBy"
        :aria-busy="pending || undefined"
        :aria-disabled="pending || undefined"
        :disabled="disabled"
        class="relative mt-0.5 inline-flex h-6 w-11 shrink-0 cursor-pointer items-center rounded-full border transition-colors duration-150 disabled:cursor-not-allowed disabled:opacity-60 aria-disabled:cursor-progress"
        :class="checked ? 'border-transparent bg-accent' : 'border-line-strong bg-raised hover:bg-raised-hover'"
        @click="!pending && emit('toggle')"
      >
        <span
          class="inline-block size-4 rounded-full shadow-sm transition-transform duration-150"
          :class="[checked ? 'translate-x-6 bg-on-accent' : 'translate-x-1 bg-ink-muted', pending ? 'animate-pulse' : '']"
        />
      </button>
    </div>
    <InlineError :id="`${id}-err`" :message="error ?? null" />
  </div>
</template>
