<script setup lang="ts">
import { computed } from 'vue'
import { api } from '../../api'
import { useActions } from '../../composables/useActions'
import { fileName } from '../../lib/format'
import ActionButton from '../ui/ActionButton.vue'
import InlineError from '../ui/InlineError.vue'
import ItemList, { type ListItem } from '../ui/ItemList.vue'
import SectionCard from '../ui/SectionCard.vue'

const props = defineProps<{ running: readonly string[]; always: readonly string[]; never: readonly string[] }>()
const actions = useActions()

const toItems = (paths: readonly string[], prefix: string): ListItem[] =>
  paths.map(path => ({ key: `${prefix}:${path}`, title: fileName(path), detail: path, icon: 'game' }))
const runningItems = computed(() => toItems(props.running, 'running'))
const alwaysItems = computed(() => toItems(props.always, 'always'))
const neverItems = computed(() => toItems(props.never, 'never'))

const pathOf = (key: string) => key.slice(key.indexOf(':') + 1)

const notAGame = (key: string) => actions.run(key, () => api.markNotAGame(pathOf(key)))
const removeAlways = (key: string) => actions.run(key, () => api.removeGame(pathOf(key), true))
const removeNever = (key: string) => actions.run(key, () => api.removeGame(pathOf(key), false))

const addGame = () => actions.run('add', async () => {
  const picked = await api.pickProgram()
  if (picked) await api.addGame(picked.path)
})
const addNotAGame = () => actions.run('add', async () => {
  const picked = await api.pickProgram()
  if (picked) await api.markNotAGame(picked.path)
})
</script>

<template>
  <SectionCard title="Games" description="Steam games are found by themselves. Add other games here, or mark a program that is not a game.">
    <div class="flex flex-wrap gap-2">
      <ActionButton icon="plus" :pending="actions.isPending('add')" @click="addGame">Add a game…</ActionButton>
      <ActionButton icon="ban" :pending="actions.isPending('add')" @click="addNotAGame">Mark a program as not a game…</ActionButton>
    </div>
    <InlineError :message="actions.errorOf('add')" />

    <div class="flex flex-col gap-1.5">
      <h3 class="text-sm font-semibold">Running now</h3>
      <ItemList :items="runningItems" label="Running games" empty-text="No game running." :error-of="actions.errorOf">
        <template #actions="{ item }">
          <ActionButton variant="btn-ghost" :pending="actions.isPending(item.key)" @click="notAGame(item.key)">Not a game</ActionButton>
        </template>
      </ItemList>
    </div>

    <div class="flex flex-col gap-1.5">
      <h3 class="text-sm font-semibold">Always a game</h3>
      <ItemList
        :items="alwaysItems"
        label="Always a game"
        empty-text="None. Use Add a game for games outside Steam."
        :is-pending="actions.isPending"
        :error-of="actions.errorOf"
        @remove="removeAlways"
      />
    </div>

    <div class="flex flex-col gap-1.5">
      <h3 class="text-sm font-semibold">Never a game</h3>
      <ItemList
        :items="neverItems"
        label="Never a game"
        empty-text="None."
        :is-pending="actions.isPending"
        :error-of="actions.errorOf"
        @remove="removeNever"
      />
    </div>
  </SectionCard>
</template>
