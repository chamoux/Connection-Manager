<script setup lang="ts" generic="T extends string">
// A radio group drawn as segments (Auto / On / Off). Arrow keys move and choose, like native radios. The selected
// segment is the service's value; choosing another asks for the change.
import { useTemplateRef } from 'vue'

const props = defineProps<{
  label: string
  options: readonly { value: T; label: string }[]
  value: T
  pending?: boolean
  disabled?: boolean
}>()
const emit = defineEmits<{ change: [value: T] }>()

const buttons = useTemplateRef<HTMLButtonElement[]>('buttons')

// Busy is aria-disabled, not disabled: a disabled button would drop keyboard focus mid-change.
function choose(value: T) {
  if (props.pending || props.disabled) return
  if (value !== props.value) emit('change', value)
}

function onKey(event: KeyboardEvent, index: number) {
  const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0
  if (step === 0) return
  event.preventDefault()
  const next = (index + step + props.options.length) % props.options.length
  const option = props.options[next]
  if (!option) return
  buttons.value?.[next]?.focus()
  choose(option.value)
}
</script>

<template>
  <div
    role="radiogroup"
    :aria-label="label"
    :aria-busy="pending || undefined"
    class="inline-flex self-start rounded-lg border border-line bg-canvas p-0.5"
    :class="{ 'opacity-60': disabled }"
  >
    <button
      v-for="(option, index) in options"
      ref="buttons"
      :key="option.value"
      type="button"
      role="radio"
      :aria-checked="option.value === value"
      :tabindex="option.value === value ? 0 : -1"
      :disabled="disabled"
      :aria-disabled="pending || undefined"
      class="h-7 min-w-14 cursor-pointer rounded-md px-3 text-sm font-medium transition-colors duration-150 disabled:cursor-not-allowed aria-disabled:cursor-progress"
      :class="option.value === value ? 'bg-raised text-ink shadow-sm ring-1 ring-line-strong' : 'text-ink-muted hover:text-ink'"
      @click="choose(option.value)"
      @keydown="onKey($event, index)"
    >
      {{ option.label }}
    </button>
  </div>
</template>
