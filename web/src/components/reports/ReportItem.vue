<script setup lang="ts">
import { computed, useId } from 'vue'
import { formatBytes, formatRange, plural } from '../../lib/format'
import type { ReportDto } from '../../types'
import ActionButton from '../ui/ActionButton.vue'
import InlineError from '../ui/InlineError.vue'
import UsageTable from '../usage/UsageTable.vue'

const props = defineProps<{ report: ReportDto; isNew: boolean; pending: boolean; error: string | null }>()
const emit = defineEmits<{ dismiss: [] }>()

const headingId = useId()
const total = computed(() => props.report.apps.reduce((sum, app) => sum + app.bytesIn + app.bytesOut, 0))
</script>

<template>
  <article class="card flex flex-col gap-3" :aria-labelledby="headingId">
    <header class="flex items-start justify-between gap-3">
      <div class="min-w-0">
        <h2 :id="headingId" class="flex items-center gap-2 text-base font-semibold">
          {{ report.game }}
          <span v-if="isNew" class="chip border-accent/40 text-accent">New</span>
        </h2>
        <p class="text-ink-muted">{{ formatRange(report.startedAt, report.endedAt) }}</p>
        <p class="mt-1">
          {{ plural(report.apps.length, 'other app') }} used <span class="font-semibold">{{ formatBytes(total) }}</span> while you played.
        </p>
      </div>
      <ActionButton v-if="isNew" variant="btn-ghost" :pending="pending" @click="emit('dismiss')">Dismiss</ActionButton>
    </header>
    <InlineError :message="error" />
    <UsageTable v-if="report.apps.length" :apps="report.apps" :caption="`Apps that used data during ${report.game}`" :allowable="false" />
  </article>
</template>
