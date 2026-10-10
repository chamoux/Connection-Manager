<script setup lang="ts">
defineProps<{ message: string | null; id?: string }>()
</script>

<template>
  <!-- The live region stays mounted so screen readers announce the message when it appears. -->
  <p :id="id" role="alert" class="text-xs font-medium text-danger empty:hidden">{{ message ?? '' }}</p>
</template>
