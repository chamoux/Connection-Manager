<script setup lang="ts">
import { useActions } from '../../composables/useActions'
import { useOptions } from '../../composables/useOptions'
import type { OptionsDto } from '../../types'
import SectionCard from '../ui/SectionCard.vue'
import ToggleRow from '../ui/ToggleRow.vue'

const props = defineProps<{ options: OptionsDto }>()
const actions = useActions()
const { save } = useOptions(actions)

type Flag = 'notifications' | 'trafficMeter' | 'detectNonSteamGames'
const flip = (flag: Flag) => save(flag, { [flag]: !props.options[flag] })
</script>

<template>
  <SectionCard title="General">
    <ToggleRow
      label="Notifications"
      description="Short tray messages, for example when Game Mode turns on and when a post-game report is ready."
      :checked="options.notifications"
      :pending="actions.isPending('notifications')"
      :error="actions.errorOf('notifications')"
      @toggle="flip('notifications')"
    />
    <ToggleRow
      label="Traffic meter"
      description="Counts how much data each app uses, for the Usage page, the app picker and the post-game reports. Nothing leaves this PC."
      :checked="options.trafficMeter"
      :pending="actions.isPending('trafficMeter')"
      :error="actions.errorOf('trafficMeter')"
      @toggle="flip('trafficMeter')"
    />
    <ToggleRow
      label="Detect games outside Steam"
      description="Treats a full-screen program that keeps the graphics card busy as a game. Browsers and video players are never counted."
      :checked="options.detectNonSteamGames"
      :pending="actions.isPending('detectNonSteamGames')"
      :error="actions.errorOf('detectNonSteamGames')"
      @toggle="flip('detectNonSteamGames')"
    />
  </SectionCard>
</template>
