<script setup lang="ts">
import { useActions } from '../../composables/useActions'
import { useOptions } from '../../composables/useOptions'
import { parseWholeNumber } from '../../lib/options'
import { Limits, type OptionsDto } from '../../types'
import SaveField from '../ui/SaveField.vue'
import SectionCard from '../ui/SectionCard.vue'
import ToggleRow from '../ui/ToggleRow.vue'

defineProps<{ options: OptionsDto }>()
const actions = useActions()
const { save } = useOptions(actions)
const range = Limits.windowsDownloadCapKBps
</script>

<template>
  <SectionCard title="Windows downloads">
    <ToggleRow
      label="Let Windows downloads through"
      description="Windows Update, Store, Office and Edge downloads, slowed to the speed below. Off keeps them offline on your hotspot."
      :checked="options.allowWindowsDownloads"
      :pending="actions.isPending('allow')"
      :error="actions.errorOf('allow')"
      @toggle="save('allow', { allowWindowsDownloads: !options.allowWindowsDownloads })"
    />
    <SaveField
      label="Speed limit"
      :description="`From ${range.min} to ${range.max.toLocaleString('en-US')} KB/s. 200 KB/s is about 700 MB an hour.`"
      :value="String(options.windowsDownloadCapKBps)"
      numeric
      suffix="KB/s"
      :maxlength="6"
      width-class="w-24"
      :disabled="!options.allowWindowsDownloads"
      :pending="actions.isPending('cap')"
      :error="actions.errorOf('cap')"
      @save="save('cap', { windowsDownloadCapKBps: parseWholeNumber($event) })"
    />
    <p class="text-xs text-ink-muted">Steam ignores this. Set Steam's own download limit in Steam, under Settings, Downloads.</p>
  </SectionCard>
</template>
