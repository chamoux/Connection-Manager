<script setup lang="ts">
import { onMounted, shallowRef } from 'vue'
import { api } from '../api'
import ReportItem from '../components/reports/ReportItem.vue'
import ActionButton from '../components/ui/ActionButton.vue'
import InlineError from '../components/ui/InlineError.vue'
import { useActions } from '../composables/useActions'
import type { ReportDto, StatusDto } from '../types'

defineProps<{ status: StatusDto }>()
const actions = useActions()
const reports = shallowRef<ReportDto[] | null>(null)

const load = () => actions.run('load', async () => { reports.value = await api.getReports() }, { refresh: false })
const dismiss = (id: string) => actions.run(id, () => api.dismissReport(id))
onMounted(load)
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex items-center justify-between gap-3">
      <p class="text-ink-muted">After each game, apps that used 5 MB or more while you played. Block the ones that should wait.</p>
      <ActionButton icon="refresh" :pending="actions.isPending('load')" @click="load">Refresh</ActionButton>
    </div>
    <InlineError :message="actions.errorOf('load')" />

    <p v-if="reports === null" class="text-ink-muted">{{ actions.isPending('load') ? 'Loading…' : 'Reports could not be loaded.' }}</p>
    <p v-else-if="reports.length === 0" class="card text-ink-muted">No reports yet. Play a game and check back here afterwards.</p>
    <template v-else>
      <ReportItem
        v-for="report in reports"
        :key="report.id"
        :report="report"
        :is-new="status.latestReport?.id === report.id"
        :pending="actions.isPending(report.id)"
        :error="actions.errorOf(report.id)"
        @dismiss="dismiss(report.id)"
      />
    </template>
  </div>
</template>
