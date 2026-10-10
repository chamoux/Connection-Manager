<script setup lang="ts">
import { computed, onMounted, shallowRef, useTemplateRef, watch, type Component } from 'vue'
import AppSidebar from './components/layout/AppSidebar.vue'
import ServiceBanner from './components/layout/ServiceBanner.vue'
import { useHashTab, type Tab } from './composables/useHashTab'
import { startService, useService } from './composables/useService'
import { dataSaverView, gameModeView } from './lib/statusText'
import DataSaverPage from './pages/DataSaverPage.vue'
import GameModePage from './pages/GameModePage.vue'
import ReportsPage from './pages/ReportsPage.vue'
import SettingsPage from './pages/SettingsPage.vue'
import StatusPage from './pages/StatusPage.vue'
import UsagePage from './pages/UsagePage.vue'

const pages: Record<Tab, { title: string; subtitle: string; component: Component }> = {
  'status': { title: 'Status', subtitle: 'What Connection Manager is doing right now.', component: StatusPage },
  'game-mode': { title: 'Game Mode', subtitle: 'Background updaters lose internet while you play, so they can’t spike your ping.', component: GameModePage },
  'data-saver': { title: 'Data Saver', subtitle: 'Only the apps you choose can use the internet on your hotspot.', component: DataSaverPage },
  'usage': { title: 'Usage', subtitle: 'How much data each app used in the last 24 hours.', component: UsagePage },
  'reports': { title: 'Reports', subtitle: 'What downloaded in the background during your games.', component: ReportsPage },
  'settings': { title: 'Settings', subtitle: 'Notifications, the traffic meter and troubleshooting.', component: SettingsPage },
}

const { status, connection, refresh } = useService()
const { current, go } = useHashTab()
const page = computed(() => pages[current.value])
const retrying = shallowRef(false)
const main = useTemplateRef<HTMLElement>('mainEl')

// The sidebar footer shows labels only, so no ticking clock is needed here.
const game = computed(() => (status.value ? gameModeView(status.value) : null))
const saver = computed(() => (status.value ? dataSaverView(status.value) : null))

async function retry() {
  retrying.value = true
  await refresh()
  retrying.value = false
}

watch(current, () => {
  main.value?.scrollTo({ top: 0 })
  document.title = `${page.value.title} – Connection Manager`
}, { immediate: true })

onMounted(startService)
</script>

<template>
  <div class="flex h-full min-h-0">
    <AppSidebar
      :current="current"
      :game="game"
      :saver="saver"
      :problem-count="status?.problems.length ?? 0"
      :has-new-report="status?.latestReport != null"
      @select="go"
    />

    <main ref="mainEl" class="@container min-w-0 flex-1 overflow-y-auto" :aria-busy="connection === 'loading' || undefined">
      <div class="mx-auto flex max-w-4xl flex-col gap-4 px-6 py-5">
        <header>
          <h1 class="text-xl font-semibold">{{ page.title }}</h1>
          <p class="text-ink-muted">{{ page.subtitle }}</p>
        </header>

        <ServiceBanner v-if="connection === 'disconnected'" :retrying="retrying" @retry="retry" />

        <div v-if="!status && connection === 'loading'" class="card flex items-center gap-3 text-ink-muted" role="status">
          <span class="spinner" aria-hidden="true" />
          Connecting to the background service…
        </div>
        <p v-else-if="!status" class="card text-ink-muted">Settings appear here once the background service answers.</p>
        <component :is="page.component" v-else :status="status" @navigate="go" />
      </div>
    </main>
  </div>
</template>
