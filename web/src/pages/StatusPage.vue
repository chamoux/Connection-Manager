<script setup lang="ts">
import { computed } from 'vue'
import { api } from '../api'
import NetworkCard from '../components/status/NetworkCard.vue'
import LatestReportCard from '../components/status/LatestReportCard.vue'
import ModeCard from '../components/status/ModeCard.vue'
import ProblemsCard from '../components/status/ProblemsCard.vue'
import { useActions } from '../composables/useActions'
import type { Tab } from '../composables/useHashTab'
import { useNow } from '../composables/useNow'
import { fileName, plural } from '../lib/format'
import { dataSaverView, gameModeView } from '../lib/statusText'
import type { ModeSetting, StatusDto } from '../types'

const props = defineProps<{ status: StatusDto }>()
const emit = defineEmits<{ navigate: [tab: Tab] }>()

const actions = useActions()
const counting = computed(() => props.status.gameActive && props.status.graceEndsAt !== null)
const now = useNow(counting)

const game = computed(() => gameModeView(props.status, now.value))
const saver = computed(() => dataSaverView(props.status))

const gameHints: Record<ModeSetting, string> = {
  auto: '',
  on: 'On: updaters stay blocked until you switch back to Auto.',
  off: 'Off: nothing is blocked during games.',
}
const saverHints: Record<ModeSetting, string> = {
  auto: 'Auto: on for your hotspot, off on Ethernet.',
  on: 'On: only allowed apps get internet, on any network.',
  off: 'Off: every app gets internet, even on your hotspot.',
}
const gameHint = computed(() => {
  if (props.status.gameMode !== 'auto') return gameHints[props.status.gameMode]
  const minutes = props.status.settings.options.graceMinutes
  return minutes === 0
    ? 'Auto: on while a game runs, off as soon as you quit.'
    : `Auto: on while a game runs, off ${plural(minutes, 'minute')} after you quit.`
})

const setGameMode = (mode: ModeSetting) => actions.run('game', () => api.setGameMode(mode))
const setDataSaver = (mode: ModeSetting) => actions.run('saver', () => api.setDataSaver(mode))
function dismissLatest() {
  const id = props.status.latestReport?.id
  if (id) void actions.run('dismiss', () => api.dismissReport(id))
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="grid gap-4 @2xl:grid-cols-2">
      <ModeCard
        title="Game Mode"
        icon="game"
        mode="game"
        :view="game"
        :setting="status.gameMode"
        :hint="gameHint"
        :pending="actions.isPending('game')"
        :error="actions.errorOf('game')"
        @change="setGameMode"
      >
        <p v-if="status.runningGames.length" class="text-xs text-ink-muted">
          Running: <span class="font-medium text-ink">{{ status.runningGames.map(fileName).join(', ') }}</span>
        </p>
      </ModeCard>
      <ModeCard
        title="Data Saver"
        icon="saver"
        mode="saver"
        :view="saver"
        :setting="status.dataSaver"
        :hint="saverHints[status.dataSaver]"
        :pending="actions.isPending('saver')"
        :error="actions.errorOf('saver')"
        @change="setDataSaver"
      />
    </div>

    <LatestReportCard
      v-if="status.latestReport"
      :report="status.latestReport"
      :pending="actions.isPending('dismiss')"
      :error="actions.errorOf('dismiss')"
      @open="emit('navigate', 'reports')"
      @dismiss="dismissLatest"
    />

    <NetworkCard :network="status.network" @manage="emit('navigate', 'data-saver')" />
    <ProblemsCard :problems="status.problems" />
  </div>
</template>
