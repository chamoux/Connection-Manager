<script setup lang="ts">
// Inline SVG icons (Lucide shapes, 24 px grid, 2 px stroke). Rendered as <path> elements: no v-html, no data: URIs.
import { computed } from 'vue'
import { icons, type IconName } from './icons'

const props = withDefaults(defineProps<{ name: IconName; size?: number }>(), { size: 16 })
const paths = computed(() => icons[props.name])
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
    class="shrink-0"
  >
    <path v-for="d in paths" :key="d" :d="d" />
  </svg>
</template>
