<script setup lang="ts">
// "Allow" (on the hotspot) and "Block" (during games) for one program. Shows a tick instead once it is on the list.
import { useActions } from '../../composables/useActions'
import { useAppLists } from '../../composables/useAppLists'
import ActionButton from '../ui/ActionButton.vue'
import AppIcon from '../ui/AppIcon.vue'
import InlineError from '../ui/InlineError.vue'

defineProps<{ path: string; name: string; allowable?: boolean }>()
const actions = useActions()
const { isAllowed, isBlocked, allow, block } = useAppLists(actions)
</script>

<template>
  <div class="flex flex-col items-end gap-1">
    <div class="flex items-center gap-0.5">
      <template v-if="allowable !== false">
        <span v-if="isAllowed(path)" class="inline-flex h-7 items-center gap-1 px-2 text-xs font-medium text-saver" :title="`${name} may use your hotspot`">
          <AppIcon name="check" :size="12" /> Allowed
        </span>
        <ActionButton
          v-else
          variant="btn-row"
          :pending="actions.isPending('allow')"
          :aria-label="`Allow ${name} on your hotspot`"
          title="Allow on your hotspot"
          @click="allow('allow', path)"
        >Allow</ActionButton>
      </template>
      <span v-if="isBlocked(path)" class="inline-flex h-7 items-center gap-1 px-2 text-xs font-medium text-game" :title="`${name} is blocked during games`">
        <AppIcon name="check" :size="12" /> Blocked
      </span>
      <ActionButton
        v-else
        variant="btn-row"
        :pending="actions.isPending('block')"
        :aria-label="`Block ${name} during games`"
        title="Block during games"
        @click="block('block', path)"
      >Block</ActionButton>
    </div>
    <InlineError :message="actions.errorOf('allow') ?? actions.errorOf('block')" class="text-right" />
  </div>
</template>
