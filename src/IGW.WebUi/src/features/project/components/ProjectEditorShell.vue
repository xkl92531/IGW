<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import type { ProjectTagRowDto, ProjectTreeNodeDto } from '../types'
import ProjectContextMenu, { type ProjectContextAction } from './ProjectContextMenu.vue'
import ProjectPropertyPanel from './ProjectPropertyPanel.vue'
import ProjectTagTable from './ProjectTagTable.vue'
import ProjectTreePane from './ProjectTreePane.vue'

const props = defineProps<{
  nodes: readonly ProjectTreeNodeDto[]
  rows: readonly ProjectTagRowDto[]
  selectedIds: readonly string[]
  total?: number
  loading?: boolean
}>()
const emit = defineEmits<{
  selectNode: [node: ProjectTreeNodeDto]
  expandNode: [node: ProjectTreeNodeDto]
  moveNode: [source: ProjectTreeNodeDto, target: ProjectTreeNodeDto]
  toggleRow: [id: string, selected: boolean]
  editRow: [row: ProjectTagRowDto]
}>()

const TREE_KEY = 'igw.project.editor.tree-width'
const PROPERTY_KEY = 'igw.project.editor.property-width'
const treeWidth = ref(Number(localStorage.getItem(TREE_KEY)) || 360)
const propertyWidth = ref(Number(localStorage.getItem(PROPERTY_KEY)) || 420)
const currentNode = ref<ProjectTreeNodeDto | null>(null)
const menu = ref<{ x: number; y: number; actions: ProjectContextAction[] } | null>(null)
let resize: { pane: 'tree' | 'property'; startX: number; startWidth: number } | null = null

function selectNode(node: ProjectTreeNodeDto) {
  menu.value = null
  currentNode.value = node
  emit('selectNode', node)
}
function showNodeMenu(event: MouseEvent, node: ProjectTreeNodeDto) {
  currentNode.value = node
  menu.value = { x: event.clientX, y: event.clientY, actions: [
    { key: 'properties', label: '属性' },
    { key: 'flow', label: 'MES 采集流程', disabled: node.kind !== 'group', reason: '仅标记组可配置采集流程' },
  ] }
}
function beginResize(pane: 'tree' | 'property', event: MouseEvent) {
  resize = { pane, startX: event.clientX, startWidth: pane === 'tree' ? treeWidth.value : propertyWidth.value }
  window.addEventListener('mousemove', onResize)
  window.addEventListener('mouseup', endResize, { once: true })
}
function onResize(event: MouseEvent) {
  if (!resize) return
  const delta = event.clientX - resize.startX
  if (resize.pane === 'tree') treeWidth.value = Math.max(300, Math.min(680, resize.startWidth + delta))
  else propertyWidth.value = Math.max(360, Math.min(720, resize.startWidth - delta))
}
function endResize() {
  if (!resize) return
  localStorage.setItem(resize.pane === 'tree' ? TREE_KEY : PROPERTY_KEY, String(resize.pane === 'tree' ? treeWidth.value : propertyWidth.value))
  resize = null
  window.removeEventListener('mousemove', onResize)
}
function closeMenu() { menu.value = null }
function toggleRow(id: string, selected: boolean) { emit('toggleRow', id, selected) }
document.addEventListener('click', closeMenu)
onBeforeUnmount(() => {
  document.removeEventListener('click', closeMenu)
  window.removeEventListener('mousemove', onResize)
  window.removeEventListener('mouseup', endResize)
})
</script>

<template>
  <div class="project-editor-shell" :style="{ '--tree-width': `${treeWidth}px`, '--property-width': `${propertyWidth}px` }">
    <ProjectTreePane :nodes="nodes" :selected-id="currentNode?.id" @select="selectNode" @expand="$emit('expandNode', $event)" @context="showNodeMenu"
      @move="(source, target) => $emit('moveNode', source, target)" />
    <div class="pane-resizer" data-resizer="tree" @mousedown="beginResize('tree', $event)" />
    <main><ProjectTagTable :rows="rows" :selected-ids="selectedIds" :total="total" :loading="loading" @toggle="toggleRow" @edit="emit('editRow', $event)" /></main>
    <div class="pane-resizer" data-resizer="property" @mousedown="beginResize('property', $event)" />
    <ProjectPropertyPanel :title="currentNode?.name" :description="currentNode?.description" :path="currentNode?.actualPath"><slot name="property" :node="currentNode" /></ProjectPropertyPanel>
    <ProjectContextMenu v-if="menu" :x="menu.x" :y="menu.y" :actions="menu.actions" @choose="closeMenu" />
  </div>
</template>

<style scoped>
.project-editor-shell { display:grid; grid-template-columns:minmax(300px,var(--tree-width)) 6px minmax(520px,1fr) 6px minmax(360px,var(--property-width)); width:100%; height:100%; overflow:hidden; border:1px solid #dce5ef; background:#eef2f7; }
main { min-width:0; overflow:hidden; background:#fff; }
.pane-resizer { cursor:col-resize; background:#edf1f5; border-inline:1px solid #dde5ee; }
.pane-resizer:hover { background:#91bde8; }
</style>
