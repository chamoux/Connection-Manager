<script setup lang="ts">
// A text or number setting with its own Save button. It starts from the service's value and follows it until edited.
import { computed, shallowRef, useId, watch } from 'vue'
import InlineError from './InlineError.vue'

const props = defineProps<{
  label: string
  description?: string
  value: string
  numeric?: boolean
  suffix?: string
  placeholder?: string
  maxlength?: number
  widthClass?: string
  pending?: boolean
  disabled?: boolean
  error?: string | null
}>()
const emit = defineEmits<{ save: [text: string] }>()

const id = useId()
const draft = shallowRef(props.value)
const dirty = computed(() => draft.value.trim() !== props.value)
const describedBy = computed(() => [props.description ? `${id}-desc` : '', props.error ? `${id}-err` : ''].filter(Boolean).join(' ') || undefined)

// A new value from the service replaces the draft unless the user is mid-edit.
watch(() => props.value, (next, previous) => {
  if (draft.value.trim() === previous) draft.value = next
})

function submit() {
  if (props.pending || props.disabled) return
  emit('save', draft.value)
}

function reset() {
  draft.value = props.value
}
</script>

<template>
  <form class="flex flex-col gap-1" novalidate @submit.prevent="submit">
    <label :for="id" class="font-medium text-ink">{{ label }}</label>
    <p v-if="description" :id="`${id}-desc`" class="text-ink-muted">{{ description }}</p>
    <div class="mt-1 flex flex-wrap items-center gap-2">
      <div class="flex items-center gap-2">
        <input
          :id="id"
          v-model="draft"
          class="field"
          :class="widthClass ?? 'w-28'"
          type="text"
          :inputmode="numeric ? 'numeric' : 'text'"
          :maxlength="maxlength"
          :placeholder="placeholder"
          :disabled="disabled"
          :aria-invalid="error ? true : undefined"
          :aria-describedby="describedBy"
          autocomplete="off"
          spellcheck="false"
          @keydown.escape.prevent="reset"
        />
        <span v-if="suffix" class="text-ink-muted">{{ suffix }}</span>
      </div>
      <button type="submit" class="btn-primary" :disabled="disabled || !dirty" :aria-disabled="pending || undefined">
        <span v-if="pending" class="spinner" aria-hidden="true" />
        {{ pending ? 'Saving' : 'Save' }}
      </button>
      <button v-if="dirty && !pending" type="button" class="btn-ghost" @click="reset">Cancel</button>
    </div>
    <InlineError :id="`${id}-err`" :message="error ?? null" />
  </form>
</template>
