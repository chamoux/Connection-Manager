<script setup lang="ts">
import ActionButton from '../ui/ActionButton.vue'
import AppIcon from '../ui/AppIcon.vue'

defineProps<{ retrying: boolean }>()
const emit = defineEmits<{ retry: [] }>()
</script>

<template>
  <div role="alert" class="flex items-start gap-3 rounded-card border border-warn/40 bg-warn/10 px-4 py-3">
    <AppIcon name="alert" :size="20" class="mt-0.5 text-warn" />
    <div class="min-w-0 flex-1">
      <p class="font-semibold text-ink">Service not running</p>
      <p class="text-ink-muted">
        The Connection Manager background service is not answering, so changes can't be saved. While it is stopped, nothing is blocked.
        Restarting the PC usually brings it back.
      </p>
    </div>
    <ActionButton icon="refresh" class="shrink-0" :pending="retrying" @click="emit('retry')">{{ retrying ? 'Checking' : 'Try again' }}</ActionButton>
  </div>
</template>
