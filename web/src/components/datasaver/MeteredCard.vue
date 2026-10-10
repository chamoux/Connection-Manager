<script setup lang="ts">
import { computed } from 'vue'
import { api } from '../../api'
import { useActions } from '../../composables/useActions'
import type { NetworkDto } from '../../types'
import SectionCard from '../ui/SectionCard.vue'
import ToggleRow from '../ui/ToggleRow.vue'

const props = defineProps<{ network: NetworkDto }>()
const actions = useActions()

const onWifi = computed(() => props.network.kind === 'wifi')
// We can switch it on only for an unrestricted network, and off only when we were the ones who switched it on.
const canChange = computed(() => onWifi.value && (props.network.canMarkMetered || props.network.markedMeteredByUs))
const note = computed(() => {
  if (!onWifi.value) return 'Connect to Wi-Fi to use this.'
  if (props.network.metered && !props.network.markedMeteredByUs) return 'Windows already treats this network as metered.'
  if (!canChange.value) return 'Windows does not allow changing this network.'
  return null
})
const networkName = computed(() => props.network.name?.trim() || 'this Wi-Fi')

const toggle = () => actions.run('metered', () => api.setMetered(!props.network.metered))
</script>

<template>
  <SectionCard title="Metered connection">
    <ToggleRow
      :label="onWifi ? `Mark ${networkName} as metered` : 'Mark the current Wi-Fi as metered'"
      description="Windows Update, OneDrive and Edge then hold off by themselves. Connection Manager puts the setting back when you turn this off or choose Restore everything."
      :checked="network.metered"
      :disabled="!canChange"
      :pending="actions.isPending('metered')"
      :error="actions.errorOf('metered')"
      @toggle="toggle"
    />
    <p v-if="note" class="text-xs text-ink-muted">{{ note }}</p>
  </SectionCard>
</template>
