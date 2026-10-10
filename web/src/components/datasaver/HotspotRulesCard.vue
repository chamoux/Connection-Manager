<script setup lang="ts">
import { computed } from 'vue'
import { api } from '../../api'
import { useActions } from '../../composables/useActions'
import { useOptions } from '../../composables/useOptions'
import { includesText } from '../../lib/format'
import type { NetworkDto, OptionsDto } from '../../types'
import ActionButton from '../ui/ActionButton.vue'
import InlineError from '../ui/InlineError.vue'
import ItemList, { type ListItem } from '../ui/ItemList.vue'
import SectionCard from '../ui/SectionCard.vue'
import ToggleRow from '../ui/ToggleRow.vue'

const props = defineProps<{ network: NetworkDto; hotspots: readonly string[]; options: OptionsDto }>()
const actions = useActions()
const { save } = useOptions(actions)

const items = computed<ListItem[]>(() => props.hotspots.map(name => ({ key: name, title: name, icon: 'wifi' })))

const currentName = computed(() => (props.network.kind === 'wifi' ? props.network.name?.trim() || null : null))
const alreadyListed = computed(() => currentName.value !== null && includesText(props.hotspots, currentName.value))
const addHint = computed(() => {
  if (props.network.kind !== 'wifi') return 'Connect to your hotspot first, then add it here.'
  if (!currentName.value) return 'Windows did not share this Wi-Fi network’s name, so it cannot be added.'
  if (alreadyListed.value) return `“${currentName.value}” is already on the list.`
  return null
})

function addCurrent() {
  const name = currentName.value
  if (name) void actions.run('add', () => api.setHotspot(name, true))
}
const remove = (name: string) => actions.run(name, () => api.setHotspot(name, false))
</script>

<template>
  <SectionCard title="When it turns on" description="In Auto, Data Saver turns on for these networks and turns off on Ethernet. iPhone hotspots are recognised by themselves.">
    <div class="flex flex-col gap-1.5">
      <div class="flex flex-wrap items-center gap-2">
        <ActionButton icon="plus" :pending="actions.isPending('add')" :disabled="addHint !== null" @click="addCurrent">
          {{ currentName && !alreadyListed ? `Add “${currentName}”` : 'Add current network' }}
        </ActionButton>
        <p v-if="addHint" class="text-xs text-ink-muted">{{ addHint }}</p>
      </div>
      <InlineError :message="actions.errorOf('add')" />
    </div>

    <ItemList
      :items="items"
      label="Your hotspots"
      empty-text="No hotspots added yet."
      :is-pending="actions.isPending"
      :error-of="actions.errorOf"
      @remove="remove"
    />

    <ToggleRow
      label="Any metered Wi-Fi"
      description="Also turn on for any Wi-Fi that Windows treats as metered. Android hotspots tell Windows they are metered."
      :checked="options.autoOnMeteredWifi"
      :pending="actions.isPending('metered-rule')"
      :error="actions.errorOf('metered-rule')"
      @toggle="save('metered-rule', { autoOnMeteredWifi: !options.autoOnMeteredWifi })"
    />
  </SectionCard>
</template>
