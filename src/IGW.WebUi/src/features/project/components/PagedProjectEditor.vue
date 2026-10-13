<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef } from 'vue'
import { projectApi, type ProjectApi } from '../api'
import type { ProjectClipboardSnapshot } from '../useProjectSelection'
import type { ProjectTagRowDto, ProjectTreeNodeDto } from '../types'
import ModbusTagEditor from '../../../components/ModbusTagEditor.vue'
import ProtocolDeviceSettings from '../../../components/ProtocolDeviceSettings.vue'
import { validateModbusTag, validateProtocolSettings } from '../../../utils/protocolEditors'
import { dataTypeEditorValue } from '../../../utils/siemensDataTypes'
import { useI18n } from '../../../i18n'
import { useProjectMutations } from '../useProjectMutations'
import { useProjectSelection } from '../useProjectSelection'
import { useProjectTree } from '../useProjectTree'
import { useTagPage } from '../useTagPage'
import ProjectBatchBar from './ProjectBatchBar.vue'
import ProjectEditorShell from './ProjectEditorShell.vue'
import ProjectExportDialog from './ProjectExportDialog.vue'
import ProjectImportDialog from './ProjectImportDialog.vue'
import ProjectToolbar from './ProjectToolbar.vue'

const props = defineProps<{ service?: ProjectApi }>()
const { t } = useI18n()
const service = props.service ?? projectApi
const tree = useProjectTree(service)
const tags = useTagPage(service)
const selection = useProjectSelection()
const children = shallowRef(new Map<string, readonly ProjectTreeNodeDto[]>())
const expanded = shallowRef(new Set<string>())
const selectedNode = shallowRef<ProjectTreeNodeDto | null>(null)
const propertyText = ref('')
const propertyError = ref('')
const busy = ref(false)
const operationLabel = ref('')
const propertyLoading = ref(false)
const actionBusy = ref(false)
const importOpen = ref(false)
const exportOpen = ref(false)
const clipboard = shallowRef<ProjectClipboardSnapshot | null>(null)
const editingRow = shallowRef<ProjectTagRowDto | null>(null)
const protocolContext = shallowRef<{ driverId: string; settings: any }>({ driverId: '', settings: {} })
let propertyRequest = 0
const propertyDocument = computed<any>({
  get() { try { return JSON.parse(propertyText.value) } catch { return null } },
  set(value) { propertyText.value = JSON.stringify(value, null, 2) },
})
const deviceSettings = computed({
  get: () => propertyDocument.value?.settings || {},
  set: (settings: any) => { propertyDocument.value = { ...propertyDocument.value, settings } },
})
const protocolEditor = computed(() => propertyDocument.value && protocolContext.value.driverId === 'Modbus.Tcp' && (editingRow.value || selectedNode.value?.kind === 'device'))

async function loadProtocolContext(node: ProjectTreeNodeDto) {
  const lineage = [node]
  let current = node
  const all = [tree.root.value, ...[...children.value.values()].flat()].filter(Boolean) as ProjectTreeNodeDto[]
  const seen = new Set([node.id])
  while (current.kind !== 'project') {
    const parentId = [...children.value].find(([, branch]) => branch.some(item => item.id === current.id))?.[0]
    const parent = all.find(item => item.id === parentId)
    if (!parent || seen.has(parent.id)) break
    lineage.push(parent); seen.add(parent.id); current = parent
  }
  const channel = lineage.find(item => item.kind === 'channel')
  const device = lineage.find(item => item.kind === 'device')
  if (!channel) return { driverId: '', settings: {} }
  const channelDocument = await service.entity('channel', channel.id)
  const driverId = String(channelDocument.driverId || '')
  const deviceDocument = device && driverId === 'Modbus.Tcp' ? await service.entity('device', device.id) : null
  return { driverId, settings: deviceDocument?.settings || {} }
}

async function editRow(row: ProjectTagRowDto) {
  const owner = selectedNode.value
  if (!owner || actionBusy.value) return
  const request = ++propertyRequest
  propertyLoading.value = true
  editingRow.value = row; propertyText.value = ''; propertyError.value = ''
  protocolContext.value = { driverId: '', settings: {} }
  try {
    const [document, context] = await Promise.all([service.entity('tag', row.id), loadProtocolContext(owner)])
    if (request !== propertyRequest) return
    protocolContext.value = context
    propertyDocument.value = { ...document, dataType: dataTypeEditorValue(document.dataType), access: ({ read: 'read', write: 'write', readwrite: 'readWrite' } as any)[String(document.access).toLowerCase()] || document.access }
  } catch (reason) { if (request === propertyRequest) propertyError.value = reason instanceof Error ? reason.message : String(reason) }
  finally { if (request === propertyRequest) propertyLoading.value = false }
}

const mutations = useProjectMutations(service, selection, {
  onRevision: () => tree.syncRevision(),
  invalidate: () => { children.value = new Map() },
})

const flatNodes = computed(() => {
  const result: ProjectTreeNodeDto[] = []
  const visit = (node: ProjectTreeNodeDto) => {
    result.push(node)
    if (!expanded.value.has(node.id)) return
    for (const child of children.value.get(node.id) || []) visit(child)
  }
  if (tree.root.value) visit(tree.root.value)
  return result
})

async function reload() {
  if (busy.value) return
  busy.value = true
  operationLabel.value = t('正在载入工程…')
  propertyError.value = ''
  try {
    children.value = new Map()
    expanded.value = new Set()
    selection.clear()
    selectedNode.value = null
    editingRow.value = null; propertyRequest++; propertyLoading.value = false
    protocolContext.value = { driverId: '', settings: {} }
    propertyText.value = ''
    await tree.loadRoot()
  } catch (reason) { propertyError.value = reason instanceof Error ? reason.message : String(reason) }
  finally { busy.value = false }
}

async function expandNode(node: ProjectTreeNodeDto) {
  if (!node.hasChildren || busy.value || actionBusy.value) return
  const nextExpanded = new Set(expanded.value)
  if (nextExpanded.has(node.id)) {
    nextExpanded.delete(node.id)
    expanded.value = nextExpanded
    return
  }
  busy.value = true
  operationLabel.value = t('正在展开工程树…')
  try {
    const branch = await tree.expand(node)
    children.value = new Map(children.value).set(node.id, branch)
    nextExpanded.add(node.id)
    expanded.value = nextExpanded
  } catch (reason) { propertyError.value = reason instanceof Error ? reason.message : String(reason) }
  finally { busy.value = false }
}

async function selectNode(node: ProjectTreeNodeDto) {
  // 双击展开会先连续产生两次 click；同一节点不重复拉属性和点位页。
  if (actionBusy.value || (selectedNode.value?.id === node.id && !editingRow.value)) return
  const request = ++propertyRequest
  propertyLoading.value = true
  editingRow.value = null
  protocolContext.value = { driverId: '', settings: {} }
  selectedNode.value = node
  selection.clear()
  selection.setContext('tag', node.id, tree.revision.value)
  propertyText.value = ''
  propertyError.value = ''
  if (node.kind === 'group') tags.selectGroup(node.id)
  else if (node.kind === 'device') tags.selectDevice(node.id)
  else { tags.items.value = []; tags.total.value = 0 }
  if (node.kind === 'project') { propertyLoading.value = false; return }
  try {
    const document = await service.entity(node.kind, node.id)
    const context = node.kind === 'device' ? await loadProtocolContext(node) : { driverId: '', settings: {} }
    if (request !== propertyRequest) return
    protocolContext.value = context
    propertyText.value = JSON.stringify(document, null, 2)
  } catch (reason) { if (request === propertyRequest) propertyError.value = reason instanceof Error ? reason.message : String(reason) }
  finally { if (request === propertyRequest) propertyLoading.value = false }
}

async function moveNode(source: ProjectTreeNodeDto, target: ProjectTreeNodeDto) {
  if (busy.value || actionBusy.value || mutations.saving.value || source.kind !== 'group' || !['device', 'group'].includes(target.kind)) return
  actionBusy.value = true
  operationLabel.value = t('正在移动标记组…')
  propertyError.value = ''
  try {
    await mutations.execute({
      kind: 'moveEntities', entityKind: 'group', entityIds: [source.id],
      targetId: target.id, documentJson: null,
    }, tree.revision.value)
    await reload()
  } catch (reason) { propertyError.value = reason instanceof Error ? reason.message : String(reason) }
  finally { actionBusy.value = false }
}

async function saveProperty() {
  if (actionBusy.value || propertyLoading.value || busy.value) return
  const node = selectedNode.value
  if (!node || node.kind === 'project' || !propertyText.value) return
  propertyError.value = ''
  actionBusy.value = true
  operationLabel.value = t('正在保存属性…')
  try {
    const document = JSON.parse(propertyText.value)
    const driverId = protocolContext.value.driverId
    if (editingRow.value && driverId === 'Modbus.Tcp') {
      const error = validateModbusTag(document, protocolContext.value.settings)
      if (error) throw new Error(error)
    }
    if (!editingRow.value && node.kind === 'device') {
      const error = validateProtocolSettings(driverId, document.settings || {})
      if (error) throw new Error(error)
    }
    if (editingRow.value && document.access === 'write' && (!document.writeConfirmation || document.writeConfirmation.mode === 'readBack')) throw new Error('只写点必须选择确认点或仅协议确认。')
    const normalized = JSON.stringify(document)
    await mutations.execute({
      kind: 'upsertEntity', entityKind: editingRow.value ? 'tag' : node.kind, entityIds: [editingRow.value?.id || node.id], targetId: null, documentJson: normalized,
    }, tree.revision.value)
    await reload()
  } catch (reason) { propertyError.value = reason instanceof Error ? reason.message : String(reason) }
  finally { actionBusy.value = false }
}

function toggleRow(id: string, selected: boolean) { selection.toggle(id, selected) }
function freeze(mode: 'copy' | 'cut') { clipboard.value = selection.freeze(mode) }
async function paste() {
  if (actionBusy.value || busy.value || !clipboard.value || selectedNode.value?.kind !== 'group') return
  const snapshot = clipboard.value, groupId = selectedNode.value.id
  actionBusy.value = true; operationLabel.value = t('正在粘贴点位…'); propertyError.value = ''
  try {
    await mutations.paste(snapshot, groupId)
    if (snapshot.mode === 'cut' && clipboard.value === snapshot) clipboard.value = null
    await tags.load()
  } catch (reason) { propertyError.value = reason instanceof Error ? reason.message : String(reason) }
  finally { actionBusy.value = false }
}
async function deleteRows() {
  if (actionBusy.value || busy.value || !selection.selectedIds.value.length) return
  const ids = [...selection.selectedIds.value]
  actionBusy.value = true; operationLabel.value = t('正在删除点位…'); propertyError.value = ''
  try {
    await mutations.execute({ kind: 'deleteEntities', entityKind: 'tag', entityIds: ids, targetId: null, documentJson: null }, tree.revision.value, ids)
    await tags.load()
  } catch (reason) { propertyError.value = reason instanceof Error ? reason.message : String(reason) }
  finally { actionBusy.value = false }
}
async function imported() { importOpen.value = false; await reload() }

onMounted(reload)
onBeforeUnmount(() => { tree.dispose(); tags.dispose() })
</script>

<template>
  <section class="paged-project-editor">
    <ProjectToolbar :busy="busy || actionBusy || propertyLoading || mutations.saving.value" :status="busy || actionBusy ? operationLabel : propertyLoading ? t('正在读取属性…') : ''" @reload="reload" @save="saveProperty" @import-json="importOpen = true" @export-json="exportOpen = true" />
    <div class="query-row">
      <input :value="tags.query.value" placeholder="搜索当前节点点位名称、说明或地址" @input="tags.search(($event.target as HTMLInputElement).value)" />
      <span v-if="tree.revision.value">修订 {{ tree.revision.value }}</span>
      <span v-if="propertyError && !selectedNode" class="error" role="alert">{{ propertyError }}</span>
      <span v-if="mutations.reloadMessage.value" class="error">{{ mutations.reloadMessage.value }}</span>
    </div>
    <ProjectBatchBar :count="selection.selectedCount.value" :busy="busy || actionBusy || mutations.saving.value" :can-paste="!!clipboard && selectedNode?.kind === 'group'"
      @copy="freeze('copy')" @cut="freeze('cut')" @paste="paste" @clear="selection.clear" />
    <div class="editor-body" :aria-busy="busy || actionBusy">
      <div v-if="busy || actionBusy" class="operation-overlay" aria-hidden="true">{{ operationLabel }}</div>
      <ProjectEditorShell :nodes="flatNodes" :rows="tags.items.value" :selected-ids="selection.selectedIds.value"
        :total="tags.total.value" :loading="tags.loading.value" @select-node="selectNode" @expand-node="expandNode" @move-node="moveNode"
        @toggle-row="toggleRow" @edit-row="editRow">
        <template #property>
          <div v-if="selectedNode && selectedNode.kind !== 'project'" class="property-editor" :aria-busy="propertyLoading" :inert="propertyLoading || actionBusy">
            <p v-if="propertyLoading" role="status">{{ t('正在读取属性…') }}</p>
            <template v-if="protocolEditor">
              <p v-if="editingRow">{{ editingRow.actualPath }}</p>
              <ModbusTagEditor v-if="editingRow" v-model="propertyDocument" :settings="protocolContext.settings" />
              <template v-else>
                <label class="endpoint-label">{{ t('端点 (IP:端口)') }}<input aria-label="Protocol endpoint" :value="propertyDocument.endpoint" @change="propertyDocument = { ...propertyDocument, endpoint: ($event.target as HTMLInputElement).value }" /></label>
                <ProtocolDeviceSettings v-model="deviceSettings" :driver-id="protocolContext.driverId" />
              </template>
              <details><summary>{{ t('其他属性（JSON）') }}</summary><textarea v-model="propertyText" spellcheck="false" /></details>
            </template>
            <textarea v-else v-model="propertyText" :disabled="propertyLoading || actionBusy" spellcheck="false" />
            <p v-if="propertyError" class="error">{{ propertyError }}</p>
            <div><button data-save-property :disabled="!propertyText || propertyLoading || actionBusy || mutations.saving.value" @click="saveProperty">保存当前属性</button><button class="danger" :disabled="!selection.selectedCount.value || actionBusy || busy" @click="deleteRows">删除选中点位</button></div>
          </div>
        </template>
      </ProjectEditorShell>
    </div>
    <footer class="pager">
      <button :disabled="tags.loading.value || actionBusy || tags.offset.value <= 0" @click="tags.goToPage(Math.floor(tags.offset.value / tags.pageSize))">上一页</button>
      <span>第 {{ Math.floor(tags.offset.value / tags.pageSize) + 1 }} 页</span>
      <button :disabled="tags.loading.value || actionBusy || tags.offset.value + tags.pageSize >= tags.total.value" @click="tags.goToPage(Math.floor(tags.offset.value / tags.pageSize) + 2)">下一页</button>
    </footer>
    <ProjectImportDialog v-model="importOpen" :service="service" @imported="imported" />
    <ProjectExportDialog v-model="exportOpen" />
  </section>
</template>

<style scoped>
.paged-project-editor { height:100%; display:grid; grid-template-rows:auto auto auto minmax(0,1fr) auto; background:#f2f5f8; }.query-row { display:flex; gap:12px; align-items:center; padding:8px 12px; border-bottom:1px solid #dce5ef; background:#fff; }.query-row input { width:min(440px,50vw); padding:8px 10px; border:1px solid #cbd7e3; border-radius:6px; }.query-row span { color:#77889a; font-size:12px; }.editor-body { position:relative; min-height:0; padding:8px; }.operation-overlay { position:absolute; inset:0; z-index:10; display:flex; align-items:center; justify-content:center; gap:10px; background:#ffffffe0; color:#2878d0; }.operation-overlay::before { content:""; width:18px; height:18px; border:2px solid #cadff5; border-top-color:#2878d0; border-radius:50%; animation:project-spin .8s linear infinite; } @keyframes project-spin { to { transform:rotate(360deg); } }.property-editor textarea { width:100%; min-height:360px; resize:vertical; padding:10px; border:1px solid #ccd7e2; border-radius:6px; font:12px/1.5 Consolas,monospace; }.property-editor div { display:flex; gap:8px; margin-top:8px; }.property-editor button,.pager button { padding:6px 11px; border:1px solid #b9c8d8; border-radius:5px; background:#fff; cursor:pointer; }.property-editor .danger { color:#cc3d3d; }.error { color:#cc3d3d!important; }.pager { display:flex; justify-content:center; gap:12px; align-items:center; padding:8px; border-top:1px solid #dce5ef; background:#fff; }
</style>
