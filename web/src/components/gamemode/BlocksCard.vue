<script setup lang="ts">
import { computed } from 'vue'
import { api } from '../../api'
import { useActions } from '../../composables/useActions'
import { fileName } from '../../lib/format'
import type { UserBlock } from '../../types'
import ActionButton from '../ui/ActionButton.vue'
import InlineError from '../ui/InlineError.vue'
import ItemList, { type ListItem } from '../ui/ItemList.vue'
import SectionCard from '../ui/SectionCard.vue'

const props = defineProps<{ blocks: readonly UserBlock[] }>()
const actions = useActions()

const keyOf = (block: UserBlock) => `${block.isFolder ? 'folder' : 'file'}:${block.path}`
const items = computed<ListItem[]>(() =>
  props.blocks.map(block => ({
    key: keyOf(block),
    title: block.isFolder ? `${fileName(block.path)} (every program in the folder)` : fileName(block.path),
    detail: block.path,
    icon: block.isFolder ? 'folder' : 'file',
  })),
)

function remove(key: string) {
  const block = props.blocks.find(b => keyOf(b) === key)
  if (block) void actions.run(key, () => api.removeBlock(block.path, block.isFolder))
}

const addProgram = () => actions.run('add', async () => {
  const picked = await api.pickProgram()
  if (picked) await api.addBlock(picked.path, false)
})
const addFolder = () => actions.run('add', async () => {
  const picked = await api.pickFolder()
  if (picked) await api.addBlock(picked.path, true)
})
</script>

<template>
  <SectionCard title="Your blocks" description="Other programs to keep offline while you play.">
    <template #actions>
      <ActionButton icon="plus" :pending="actions.isPending('add')" @click="addProgram">Add program…</ActionButton>
      <ActionButton icon="folder" :pending="actions.isPending('add')" @click="addFolder">Add folder…</ActionButton>
    </template>
    <InlineError :message="actions.errorOf('add')" />
    <ItemList
      :items="items"
      label="Your blocks"
      empty-text="Nothing added yet. Add a program, or a folder to block every program inside it."
      :is-pending="actions.isPending"
      :error-of="actions.errorOf"
      @remove="remove"
    />
  </SectionCard>
</template>
