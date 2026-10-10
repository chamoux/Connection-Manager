<script setup lang="ts">
import { computed, shallowRef } from 'vue'
import { api } from '../../api'
import { useActions } from '../../composables/useActions'
import { fileName } from '../../lib/format'
import ActionButton from '../ui/ActionButton.vue'
import InlineError from '../ui/InlineError.vue'
import ItemList, { type ListItem } from '../ui/ItemList.vue'
import SectionCard from '../ui/SectionCard.vue'
import UsagePicker from './UsagePicker.vue'

const props = defineProps<{ allowed: readonly string[]; meterOn: boolean }>()
const actions = useActions()
const showPicker = shallowRef(false)

const items = computed<ListItem[]>(() => props.allowed.map(path => ({ key: path, title: fileName(path), detail: path, icon: 'file' })))

const remove = (path: string) => actions.run(path, () => api.removeAllowedApp(path))
const addProgram = () => actions.run('add', async () => {
  const picked = await api.pickProgram()
  if (picked) await api.allowApp(picked.path)
})
</script>

<template>
  <SectionCard
    title="Allowed apps"
    description="Only the apps you choose can use the internet on your hotspot. Steam, your running game, security software and Windows' essentials always can."
  >
    <div class="flex flex-wrap gap-2">
      <ActionButton icon="plus" :pending="actions.isPending('add')" @click="addProgram">Add program…</ActionButton>
      <button type="button" class="btn" :aria-expanded="showPicker" aria-controls="usage-picker" @click="showPicker = !showPicker">
        {{ showPicker ? 'Hide recent apps' : 'Choose from recent apps' }}
      </button>
    </div>
    <InlineError :message="actions.errorOf('add')" />

    <div v-if="showPicker" id="usage-picker">
      <UsagePicker :allowed="allowed" :meter-on="meterOn" />
    </div>

    <ItemList
      :items="items"
      label="Allowed apps"
      empty-text="No apps allowed yet. On your hotspot, only Steam, your game and Windows' essentials get internet."
      :is-pending="actions.isPending"
      :error-of="actions.errorOf"
      @remove="remove"
    />
  </SectionCard>
</template>
