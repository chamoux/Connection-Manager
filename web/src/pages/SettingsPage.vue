<script setup lang="ts">
import GeneralCard from '../components/settings/GeneralCard.vue'
import TroubleshootCard from '../components/settings/TroubleshootCard.vue'
import UploadCapsCard from '../components/settings/UploadCapsCard.vue'
import type { StatusDto } from '../types'

defineProps<{ status: StatusDto }>()
</script>

<template>
  <div class="flex flex-col gap-4">
    <GeneralCard :options="status.settings.options" />
    <UploadCapsCard :options="status.settings.options" :caps="status.settings.uploadCaps" />
    <TroubleshootCard />
  </div>
</template>
