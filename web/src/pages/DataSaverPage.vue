<script setup lang="ts">
import AllowedAppsCard from '../components/datasaver/AllowedAppsCard.vue'
import HotspotRulesCard from '../components/datasaver/HotspotRulesCard.vue'
import MeteredCard from '../components/datasaver/MeteredCard.vue'
import WindowsDownloadsCard from '../components/datasaver/WindowsDownloadsCard.vue'
import type { StatusDto } from '../types'

defineProps<{ status: StatusDto }>()
</script>

<template>
  <div class="flex flex-col gap-4">
    <HotspotRulesCard :network="status.network" :hotspots="status.settings.hotspotNetworks" :options="status.settings.options" />
    <AllowedAppsCard :allowed="status.settings.allowApps" :meter-on="status.settings.options.trafficMeter" />
    <WindowsDownloadsCard :options="status.settings.options" />
    <MeteredCard :network="status.network" />
  </div>
</template>
