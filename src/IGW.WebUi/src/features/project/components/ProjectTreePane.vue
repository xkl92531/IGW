<script setup lang="ts">
import { shallowRef } from 'vue'
import type { ProjectTreeNodeDto } from '../types'

defineProps<{ nodes: readonly ProjectTreeNodeDto[]; selectedId?: string }>()
const emit = defineEmits<{
  select: [node: ProjectTreeNodeDto]
  expand: [node: ProjectTreeNodeDto]
  context: [event: MouseEvent, node: ProjectTreeNodeDto]
  move: [source: ProjectTreeNodeDto, target: ProjectTreeNodeDto]
}>()

const draggedNode = shallowRef<ProjectTreeNodeDto | null>(null)
const dropTargetId = shallowRef('')

function canDrop(source: ProjectTreeNodeDto | null, target: ProjectTreeNodeDto) {
  if (!source || source.kind !== 'group' || !['device', 'group'].includes(target.kind)) return false
  if (source.id === target.id) return false
  return !target.actualPath.toLocaleLowerCase().startsWith(`${source.actualPath.toLocaleLowerCase()}.`)
}

function startDrag(event: DragEvent, node: ProjectTreeNodeDto) {
  if (node.kind !== 'group') return
  draggedNode.value = node
  event.dataTransfer?.setData('text/plain', node.id)
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
}

function dragOver(event: DragEvent, node: ProjectTreeNodeDto) {
  if (!canDrop(draggedNode.value, node)) return
  event.preventDefault()
  dropTargetId.value = node.id
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
}

function drop(event: DragEvent, target: ProjectTreeNodeDto) {
  const source = draggedNode.value
  if (!canDrop(source, target)) return clearDrag()
  event.preventDefault()
  emit('move', source!, target)
  clearDrag()
}

function clearDrag() {
  draggedNode.value = null
  dropTargetId.value = ''
}
</script>

<template>
  <nav class="project-tree-pane" aria-label="工程层级">
    <button v-for="node in nodes" :key="node.id" type="button" class="tree-node"
      :class="{ 'is-current': selectedId === node.id, 'is-dragging': draggedNode?.id === node.id, 'is-drop-target': dropTargetId === node.id }"
      :data-node-id="node.id" :draggable="node.kind === 'group'"
      :title="`${node.actualPath}${node.description ? `\n${node.description}` : ''}`"
      @click="$emit('select', node)" @dblclick="$emit('expand', node)"
      @contextmenu.prevent="$emit('context', $event, node)"
      @dragstart="startDrag($event, node)" @dragover="dragOver($event, node)"
      @dragleave="dropTargetId === node.id && (dropTargetId = '')" @drop="drop($event, node)" @dragend="clearDrag">
      <span class="tree-icon" aria-hidden="true">▸</span>
      <span class="tree-copy">
        <strong>{{ node.name }}</strong><small v-if="node.description">{{ node.description }}</small>
        <span>{{ node.actualPath }}</span>
      </span>
      <em v-if="node.directTagCount">{{ node.directTagCount }}</em>
    </button>
  </nav>
</template>

<style scoped>
.project-tree-pane { height:100%; overflow:auto; padding:8px; background:#f8fafc; }
.tree-node { width:100%; display:grid; grid-template-columns:16px minmax(0,1fr) auto; gap:6px; align-items:center; padding:8px; margin:0 0 4px; text-align:left; border:1px solid transparent; border-radius:6px; background:transparent; color:#2f4054; cursor:pointer; }
.tree-node:hover { background:#eef5fd; }
.tree-node.is-current { border-color:#8abdf1; background:#eaf3ff; box-shadow:inset 3px 0 #409eff; }
.tree-node.is-dragging { opacity:.55; }
.tree-node.is-drop-target { border-color:#409eff; background:#e2f1ff; box-shadow:inset 0 0 0 1px #409eff; }
.tree-copy { min-width:0; display:grid; grid-template-columns:auto minmax(0,1fr); column-gap:6px; }
.tree-copy strong,.tree-copy small,.tree-copy span { overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
.tree-copy small { color:#6f7f91; }
.tree-copy span { grid-column:1/-1; color:#8493a5; font-size:11px; }
.tree-node em { color:#718399; font-style:normal; font-size:11px; }
</style>
