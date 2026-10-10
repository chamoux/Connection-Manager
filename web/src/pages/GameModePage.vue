<script setup lang="ts">
import BlocksCard from '../components/gamemode/BlocksCard.vue'
import CatalogCard from '../components/gamemode/CatalogCard.vue'
import GameOptionsCard from '../components/gamemode/GameOptionsCard.vue'
import GamesCard from '../components/gamemode/GamesCard.vue'
import type { StatusDto } from '../types'

defineProps<{ status: StatusDto }>()
</script>

<template>
  <div class="flex flex-col gap-4">
    <CatalogCard :catalog="status.settings.catalog" />
    <BlocksCard :blocks="status.settings.extraBlocks" />
    <GamesCard :running="status.runningGames" :always="status.settings.alwaysGames" :never="status.settings.neverGames" />
    <GameOptionsCard :options="status.settings.options" />
  </div>
</template>
