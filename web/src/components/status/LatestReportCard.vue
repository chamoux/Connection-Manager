<script setup lang="ts">
import { formatBytes, formatDateTime, plural } from '../../lib/format'
import type { ReportSummaryDto } from '../../types'
import ActionButton from '../ui/ActionButton.vue'
import InlineError from '../ui/InlineError.vue'
import SectionCard from '../ui/SectionCard.vue'

defineProps<{ report: ReportSummaryDto; pending: boolean; error: string | null }>()
const emit = defineEmits<{ open: []; dismiss: [] }>()
</script>

<template>
  <SectionCard title="After your last game">
    <div>
      <p>
        While you played <span class="font-semibold">{{ report.game }}</span>,
        {{ plural(report.appCount, 'other app') }} used <span class="font-semibold">{{ formatBytes(report.totalBytes) }}</span>.
      </p>
      <p class="text-xs text-ink-muted">Ended {{ formatDateTime(report.endedAt) }}</p>
    </div>
    <div class="flex flex-wrap items-center gap-2">
      <button type="button" class="btn-primary" @click="emit('open')">See the report</button>
      <ActionButton variant="btn-ghost" :pending="pending" @click="emit('dismiss')">Dismiss</ActionButton>
    </div>
    <InlineError :message="error" />
  </SectionCard>
</template>
