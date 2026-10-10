<script setup lang="ts">
import { computed, onMounted, shallowRef } from 'vue'
import { api } from '../api'
import UsageTable from '../components/usage/UsageTable.vue'
import ActionButton from '../components/ui/ActionButton.vue'
import InlineError from '../components/ui/InlineError.vue'
import SectionCard from '../components/ui/SectionCard.vue'
import { useActions } from '../composables/useActions'
import { formatBytes } from '../lib/format'
import type { StatusDto, UsageDto } from '../types'

defineProps<{ status: StatusDto }>()
const actions = useActions()
const usage = shallowRef<UsageDto[] | null>(null)
const total = computed(() => (usage.value ?? []).reduce((sum, app) => sum + app.bytesIn + app.bytesOut, 0))

const load = () => actions.run('load', async () => { usage.value = await api.getUsage() }, { refresh: false })
onMounted(load)
</script>

<template>
  <SectionCard
    title="Apps in the last 24 hours"
    :description="usage && usage.length ? `${formatBytes(total)} in total. Allow lets an app use your hotspot; Block keeps it offline during games.` : undefined"
  >
    <template #actions>
      <ActionButton icon="refresh" :pending="actions.isPending('load')" @click="load">Refresh</ActionButton>
    </template>

    <p v-if="!status.settings.options.trafficMeter" class="rounded-lg border border-line bg-raised/50 px-3 py-2 text-ink-muted">
      The traffic meter is off, so nothing new is recorded. Turn it on in Settings.
    </p>
    <InlineError :message="actions.errorOf('load')" />

    <p v-if="usage === null" class="text-ink-muted">{{ actions.isPending('load') ? 'Loading…' : 'Usage could not be loaded.' }}</p>
    <p v-else-if="usage.length === 0" class="text-ink-muted">No app has used the internet in the last 24 hours.</p>
    <UsageTable v-else :apps="usage" caption="Data used per app in the last 24 hours" />
  </SectionCard>
</template>
