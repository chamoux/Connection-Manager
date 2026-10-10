<script setup lang="ts">
import { nextTick, shallowRef, useTemplateRef } from 'vue'
import { api } from '../../api'
import { useActions } from '../../composables/useActions'
import ActionButton from '../ui/ActionButton.vue'
import InlineError from '../ui/InlineError.vue'
import SectionCard from '../ui/SectionCard.vue'

const actions = useActions()
// The window has no confirm() dialogs, so the confirmation is inline.
const confirming = shallowRef(false)
const restored = shallowRef(false)
const startButton = useTemplateRef<HTMLButtonElement>('startBtn')
const cancelButton = useTemplateRef<HTMLButtonElement>('cancelBtn')

async function ask() {
  restored.value = false
  confirming.value = true
  await nextTick()
  cancelButton.value?.focus()
}

async function cancel() {
  confirming.value = false
  await nextTick()
  startButton.value?.focus()
}

async function restore() {
  if (await actions.run('restore', () => api.restoreEverything())) {
    restored.value = true
    await cancel()
  }
}

const openLogs = () => actions.run('logs', () => api.openLogFolder(), { refresh: false })
</script>

<template>
  <SectionCard title="Troubleshooting">
    <div class="flex flex-col gap-2">
      <div>
        <p class="font-medium">Restore everything</p>
        <p class="text-ink-muted">
          Puts back the Windows settings Connection Manager changed: the Windows download speed limit and the metered setting on your Wi-Fi.
        </p>
      </div>
      <div v-if="!confirming" class="flex flex-wrap items-center gap-3">
        <button ref="startBtn" type="button" class="btn-danger" @click="ask">Restore everything…</button>
        <p v-if="restored" role="status" class="text-game">Done. Windows settings are back to how they were.</p>
      </div>
      <div v-else class="flex flex-wrap items-center gap-2 rounded-lg border border-danger/40 bg-danger/5 px-3 py-2" role="group" aria-label="Confirm restore">
        <p class="mr-auto">Put everything back now?</p>
        <ActionButton variant="btn-danger" icon="restore" :pending="actions.isPending('restore')" @click="restore">Restore</ActionButton>
        <button ref="cancelBtn" type="button" class="btn-ghost" @click="cancel">Cancel</button>
      </div>
      <InlineError :message="actions.errorOf('restore')" />
    </div>

    <div class="flex flex-col gap-2 border-t border-line pt-3">
      <div>
        <p class="font-medium">Log files</p>
        <p class="text-ink-muted">Each day's log shows what Connection Manager blocked and why. Useful when something doesn't work.</p>
      </div>
      <div>
        <ActionButton icon="folder" :pending="actions.isPending('logs')" @click="openLogs">Open log folder</ActionButton>
      </div>
      <InlineError :message="actions.errorOf('logs')" />
    </div>
  </SectionCard>
</template>
