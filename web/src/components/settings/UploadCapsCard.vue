<script setup lang="ts">
// Per-app upload caps: pick a program, give it a speed, and the service sets a Windows QoS policy for it.
// One cap per program: choosing a capped program again, or "Change", replaces its speed.
import { computed, nextTick, shallowRef, useId, useTemplateRef } from 'vue'
import { api } from '../../api'
import { useActions } from '../../composables/useActions'
import { useOptions } from '../../composables/useOptions'
import { fileName, sameText } from '../../lib/format'
import { checkUploadCapKBps, formatKBps } from '../../lib/uploadCaps'
import { Limits, type OptionsDto, type UploadCapArgs } from '../../types'
import ActionButton from '../ui/ActionButton.vue'
import AppIcon from '../ui/AppIcon.vue'
import InlineError from '../ui/InlineError.vue'
import ItemList, { type ListItem } from '../ui/ItemList.vue'
import SectionCard from '../ui/SectionCard.vue'
import ToggleRow from '../ui/ToggleRow.vue'

const DEFAULT_KBPS = 500

const props = defineProps<{ options: OptionsDto; caps: readonly UploadCapArgs[] }>()
const actions = useActions()
const { save } = useOptions(actions)
const range = Limits.uploadCapKBps
const id = useId()
const rateInput = useTemplateRef<HTMLInputElement>('rateEl')

/** The program waiting for its speed, after "Add app…" or "Change". */
const editing = shallowRef<string | null>(null)
const draft = shallowRef('')
const touched = shallowRef(false)

const items = computed<ListItem[]>(() => props.caps.map(cap => ({ key: cap.path, title: fileName(cap.path), detail: cap.path, icon: 'file' })))
const rateOf = (path: string) => props.caps.find(cap => sameText(cap.path, path))?.kBps ?? null
const replacing = computed(() => editing.value !== null && rateOf(editing.value) !== null)
const full = computed(() => props.caps.length >= Limits.maxUploadCaps)
const rateError = computed(() => (touched.value ? checkUploadCapKBps(draft.value).error : null) ?? actions.errorOf('save'))
const describedBy = computed(() => `${id}-desc${rateError.value ? ` ${id}-err` : ''}`)

async function edit(path: string) {
  editing.value = path
  draft.value = String(rateOf(path) ?? DEFAULT_KBPS)
  touched.value = false
  actions.clearError('save')
  await nextTick()
  rateInput.value?.select()
}

const pick = () => actions.run('pick', async () => {
  const picked = await api.pickProgram()
  if (picked) await edit(picked.path)
}, { refresh: false })

async function submit() {
  const path = editing.value
  if (!path || actions.isPending('save')) return
  touched.value = true
  const check = checkUploadCapKBps(draft.value)
  if (check.error !== null) {
    rateInput.value?.focus()
    return
  }
  if (await actions.run('save', () => api.setUploadCap({ path, kBps: check.kBps }))) editing.value = null
}

function cancel() {
  editing.value = null
  actions.clearError('save')
}

const remove = (path: string) => {
  if (!actions.isPending(path)) void actions.run(path, () => api.removeUploadCap(path))
}
</script>

<template>
  <SectionCard title="Upload caps" description="Limit how fast chosen apps can upload, so a backup or a cloud sync doesn't fill your connection.">
    <ToggleRow
      label="Use upload caps"
      description="Off takes the caps out of Windows but keeps your list."
      :checked="options.uploadCapsEnabled"
      :pending="actions.isPending('enabled')"
      :error="actions.errorOf('enabled')"
      @toggle="save('enabled', { uploadCapsEnabled: !options.uploadCapsEnabled })"
    />

    <div class="flex flex-col gap-1.5">
      <div class="flex flex-wrap items-center gap-2">
        <ActionButton icon="plus" :pending="actions.isPending('pick')" :disabled="full" @click="pick">Add app…</ActionButton>
        <p v-if="full" class="text-xs text-ink-muted">{{ Limits.maxUploadCaps }} apps is the most. Remove one to add another.</p>
      </div>
      <InlineError :message="actions.errorOf('pick')" />
    </div>

    <form v-if="editing" class="flex flex-col gap-1 rounded-lg border border-line bg-canvas/40 p-3" novalidate @submit.prevent="submit">
      <p class="truncate font-medium" :title="fileName(editing)">{{ replacing ? 'Change the cap for' : 'Cap' }} {{ fileName(editing) }}</p>
      <p class="truncate font-mono text-xs text-ink-muted" :title="editing">{{ editing }}</p>
      <label :for="id" class="mt-2 font-medium text-ink">Upload speed limit</label>
      <p :id="`${id}-desc`" class="text-ink-muted">
        From {{ range.min }} to {{ range.max.toLocaleString('en-US') }} KB/s. 100 KB/s is about 350 MB an hour.
      </p>
      <div class="mt-1 flex flex-wrap items-center gap-2">
        <div class="flex items-center gap-2">
          <input
            :id="id"
            ref="rateEl"
            v-model="draft"
            class="field w-28"
            type="text"
            inputmode="numeric"
            maxlength="7"
            :aria-invalid="rateError ? true : undefined"
            :aria-describedby="describedBy"
            autocomplete="off"
            spellcheck="false"
            @blur="touched = true"
            @keydown.escape.prevent="cancel"
          />
          <span class="text-ink-muted">KB/s</span>
        </div>
        <button type="submit" class="btn-primary" :aria-disabled="actions.isPending('save') || undefined">
          <span v-if="actions.isPending('save')" class="spinner" aria-hidden="true" />
          {{ actions.isPending('save') ? 'Saving' : replacing ? 'Save' : 'Add cap' }}
        </button>
        <button v-if="!actions.isPending('save')" type="button" class="btn-ghost" @click="cancel">Cancel</button>
      </div>
      <InlineError :id="`${id}-err`" :message="rateError" />
    </form>

    <ItemList
      :items="items"
      label="Upload caps"
      empty-text="No caps yet. Add an app that uploads a lot, such as a backup or cloud sync tool."
      :is-pending="actions.isPending"
      :error-of="actions.errorOf"
      @remove="remove"
    >
      <template #actions="{ item }">
        <span class="chip tabular-nums">{{ formatKBps(rateOf(item.key) ?? 0) }}</span>
        <button type="button" class="btn-row" :aria-label="`Change the cap for ${item.title}`" @click="edit(item.key)">Change</button>
        <button
          type="button"
          class="btn-icon"
          :aria-label="`Remove the cap for ${item.title}`"
          title="Remove"
          :aria-disabled="actions.isPending(item.key) || undefined"
          @click="remove(item.key)"
        >
          <span v-if="actions.isPending(item.key)" class="spinner" aria-hidden="true" />
          <AppIcon v-else name="x" />
        </button>
      </template>
    </ItemList>

    <p class="text-xs text-ink-muted">
      Upload caps slow how fast an app can send data. They don't limit downloads. They reset when you restart the PC and come back when Connection Manager starts.
    </p>
  </SectionCard>
</template>
