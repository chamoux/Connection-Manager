<script setup lang="ts">
// A button that shows a spinner while its call runs. Busy is aria-disabled rather than disabled, so keyboard focus stays put.
import AppIcon from './AppIcon.vue'
import type { IconName } from './icons'

const props = withDefaults(defineProps<{
  pending?: boolean
  disabled?: boolean
  variant?: 'btn' | 'btn-primary' | 'btn-ghost' | 'btn-danger' | 'btn-row'
  icon?: IconName
}>(), { variant: 'btn' })
const emit = defineEmits<{ click: [] }>()

function onClick() {
  if (!props.pending) emit('click')
}
</script>

<template>
  <button type="button" :class="variant" :disabled="disabled" :aria-disabled="pending || undefined" @click="onClick">
    <span v-if="pending" class="spinner" aria-hidden="true" />
    <AppIcon v-else-if="icon" :name="icon" />
    <slot />
  </button>
</template>
