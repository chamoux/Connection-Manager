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
</script>

<template>
  <SectionCard title="Timing and hotkey">
    <SaveField
      label="Grace period"
      description="How long Game Mode stays on after a game closes, so it doesn't flicker when a game restarts."
      :value="String(options.graceMinutes)"
      numeric
      suffix="minutes (0 to 30)"
      :maxlength="2"
      width-class="w-16"
      :pending="actions.isPending('grace')"
      :error="actions.errorOf('grace')"
      @save="save('grace', { graceMinutes: parseWholeNumber($event) })"
    />
    <SaveField
      label="Hotkey"
      description="Press it to force Game Mode on, and again to go back to Auto. Use Ctrl, Alt, Shift or Win with a letter, digit or F1 to F24."
      :value="options.hotkey"
      placeholder="Ctrl+Alt+G"
      :maxlength="Limits.hotkeyMaxLength"
      width-class="w-40"
      :pending="actions.isPending('hotkey')"
      :error="actions.errorOf('hotkey')"
      @save="save('hotkey', { hotkey: $event })"
    />
    <ToggleRow
      label="Pause Wi-Fi scans during games"
      description="Windows looks for Wi-Fi networks every minute, and each scan can spike your ping. Only applies on Wi-Fi."
      :checked="options.wifiTuning"
      :pending="actions.isPending('wifi')"
      :error="actions.errorOf('wifi')"
      @toggle="save('wifi', { wifiTuning: !options.wifiTuning })"
    />
  </SectionCard>
</template>
