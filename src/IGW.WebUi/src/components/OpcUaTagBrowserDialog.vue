<script setup lang="ts">
import { computed, markRaw, nextTick, onUnmounted, ref, shallowRef, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api, type OpcUaBrowsedTag, type OpcUaBrowsePreviewBody, type OpcUaBrowseIssue } from '../api'
import { buildImportPreview, type ImportedTag, type TagImportPreview } from '../utils/kepTagImport'
import {
  defaultOpcUaGroupName, folderAliasKey, opcUaDataTypeName, opcUaTagAliasKey, structuredOpcUaTagPath,
  toImportedOpcUaTag, validateOpcUaAliases,
  type OpcUaImportAliases,
} from '../utils/opcUaStructuredImport'
import { useI18n } from '../i18n'
import {
  browseOperationElapsed, browseResultMetrics, type OpcUaBrowseOperation,
  buildSelectedBranchRows, flattenSelectedBranchTags, isLazyBrowseRootNode,
  normalizeOpcUaPathSegments, prepareOpcUaImport, toLazyBrowseTreeNode, type OpcUaLazyTreeNode,
} from '../utils/opcUaLazyBrowse'

const { t: tr } = useI18n()

const props = withDefaults(defineProps<{
  modelValue: boolean
  connection: OpcUaBrowsePreviewBody
  target: any
  deviceName?: string
  /** false=工程配置只能看：只浏览，不导入 */
  editable?: boolean
}>(), { editable: true })
const emit = defineEmits<{
  'update:modelValue': [boolean]
  apply: [{
    tags: ImportedTag[]
    overwriteConflicts: boolean
    sourceDeviceName?: string
    targetDeviceName?: string
  }]
}>()

interface BrowseTreeNode {
  id: string
  label: string
  path: string
  sourceSegments: string[]
  folder: boolean
  tag?: OpcUaBrowsedTag
  children?: BrowseTreeNode[]
  nodeId?: string
  isVariable?: boolean
  isLeaf?: boolean
}

const browseOperations = ref<OpcUaBrowseOperation[]>([])
const operationClock = ref(0)
let operationSequence = 0
let elapsedTimer: ReturnType<typeof setInterval> | undefined
function stopElapsedTimer() {
  if (elapsedTimer !== undefined) clearInterval(elapsedTimer)
  elapsedTimer = undefined
}
function beginBrowseOperation(stage: string, label: string) {
  const operation: OpcUaBrowseOperation = { id: ++operationSequence, stage, label, startedAt: performance.now() }
  browseOperations.value = [...browseOperations.value.filter(item => item.finishedAt === undefined),
    ...browseOperations.value.filter(item => item.finishedAt !== undefined).slice(-4), operation]
  operationClock.value = operation.startedAt
  if (elapsedTimer === undefined)
    elapsedTimer = setInterval(() => { operationClock.value = performance.now() }, 250)
  return operation.id
}
function finishBrowseOperation(id: number, failed: boolean, result?: unknown) {
  const operation = browseOperations.value.find(item => item.id === id)
  if (!operation) return
  operation.finishedAt = performance.now()
  operation.failed = failed
  Object.assign(operation, browseResultMetrics(result))
  operationClock.value = operation.finishedAt
  if (browseOperations.value.every(item => item.finishedAt !== undefined)) stopElapsedTimer()
}
const visibleOperations = computed(() => [...browseOperations.value].sort((a, b) =>
  Number(a.finishedAt !== undefined) - Number(b.finishedAt !== undefined) || b.id - a.id).slice(0, 5))
const completedBranchCount = computed(() => Object.keys(selectedBranches.value).length)
const loading = ref(false)
const error = ref('')
const truncated = ref(false)
const tags = ref<OpcUaBrowsedTag[]>([])
const lazyTree = ref<OpcUaLazyTreeNode[]>([])
const selectedBranches = ref<Record<string, OpcUaBrowsedTag[]>>({})
const truncatedBranches = ref<Record<string, boolean>>({})
const selectedBranchNodes = ref<Record<string, OpcUaLazyTreeNode>>({})
const branchErrors = ref<Record<string, string>>({})
const branchIssues = ref<Record<string, OpcUaBrowseIssue[]>>({})
const issueRetries = ref<string[]>([])
const issuePageSize = 50
const issuePage = ref(1)
function browseIssueKey(branchId: string, issue: OpcUaBrowseIssue) {
  return JSON.stringify([branchId, issue.nodeId, issue.pathSegments])
}
function sameBrowseIssue(left: OpcUaBrowseIssue, right: OpcUaBrowseIssue) {
  return left.nodeId === right.nodeId && left.pathSegments.length === right.pathSegments.length &&
    left.pathSegments.every((segment, index) => segment === right.pathSegments[index])
}
const issueCount = computed(() => checkedKeys.value.reduce((total, branchId) => total + (branchIssues.value[branchId]?.length || 0), 0))
const issuePageCount = computed(() => Math.max(1, Math.ceil(issueCount.value / issuePageSize)))
const visibleIssues = computed(() => {
  const rows: (OpcUaBrowseIssue & { branchId: string; issueKey: string })[] = []
  let skip = (Math.min(issuePage.value, issuePageCount.value) - 1) * issuePageSize
  for (const branchId of checkedKeys.value) {
    const issues = branchIssues.value[branchId] || []
    if (skip >= issues.length) { skip -= issues.length; continue }
    rows.push(...issues.slice(skip, skip + issuePageSize - rows.length).map(issue => ({ ...issue, branchId, issueKey: browseIssueKey(branchId, issue) })))
    skip = 0
    if (rows.length === issuePageSize) break
  }
  return rows
})
const branchLoadingKeys = ref<string[]>([])
const query = ref('')
const checkedKeys = ref<string[]>([])
const overwriteConflicts = ref(false)
const treeRef = ref<any>()
const treeProps = { value: 'id', label: 'label', children: 'children' }
const lazyTreeProps = { label: 'label', children: 'children', isLeaf: 'isLeaf' }
const rootGroupName = ref('')
const folderNames = ref<Record<string, string>>({})
const tagNames = ref<Record<string, string>>({})
const selectedNode = ref<BrowseTreeNode | null>(null)
const targetDeviceName = ref('')
const targetDeviceEdited = ref(false)
const preparingImport = ref(false)
const preparedRawTags = shallowRef<OpcUaBrowsedTag[]>([])
const preparedSelectedTags = shallowRef<ImportedTag[]>([])
const preparedDeviceRoots = shallowRef<string[]>([])
const preparedSourceDeviceName = ref('')
const preparedTargetError = ref('')
const preparationError = ref('')
const emptyPreview = (): TagImportPreview => ({ tags: [], conflicts: [], groupsToCreate: 0, skipped: 0 })
const preparedPreview = shallowRef<TagImportPreview>(emptyPreview())
let browseGeneration = 0
let preparationGeneration = 0

function clearPreparedImport() {
  preparedRawTags.value = []
  preparedSelectedTags.value = []
  preparedDeviceRoots.value = []
  preparedSourceDeviceName.value = ''
  preparedTargetError.value = ''
  preparedPreview.value = emptyPreview()
  preparationError.value = ''
}
function cancelPreparation() {
  preparationGeneration++
  preparingImport.value = false
}
function cancelBrowseContext() {
  browseGeneration++
  cancelPreparation()
  stopElapsedTimer()
}
onUnmounted(cancelBrowseContext)

const dataTypeName = opcUaDataTypeName
const opcUaPreview = computed(() => !props.deviceName)
const selectionLoading = computed(() => branchLoadingKeys.value.length > 0 || issueRetries.value.length > 0 || preparingImport.value)
const anyTruncated = computed(() => truncated.value || Object.values(truncatedBranches.value).some(Boolean))
const selectedBranchRows = computed(() => buildSelectedBranchRows(
  checkedKeys.value,
  selectedBranchNodes.value,
  selectedBranches.value,
  branchLoadingKeys.value,
  branchErrors.value,
  truncatedBranches.value,
))
const tagKey = (tag: OpcUaBrowsedTag) => `tag:${opcUaTagAliasKey(tag)}`
const aliases = computed<OpcUaImportAliases>(() => ({
  rootGroupName: rootGroupName.value,
  folderNames: folderNames.value,
  tagNames: tagNames.value,
}))
const aliasErrors = computed(() => validateOpcUaAliases(aliases.value))

function buildTree(source: OpcUaBrowsedTag[]): BrowseTreeNode[] {
  const roots: BrowseTreeNode[] = []
  const folders = new Map<string, BrowseTreeNode>()
  for (const tag of source) {
    const structuredPath = structuredOpcUaTagPath(tag)
    const parts = structuredPath.folderSegments
    let children = roots
    const path: string[] = []
    for (const part of parts) {
      path.push(part)
      const key = path.join('\u0000')
      let folder = folders.get(key)
      if (!folder) {
        folder = { id: `folder:${key}`, label: part, path: path.join('.'), sourceSegments: [...path], folder: true, children: [] }
        folders.set(key, folder)
        children.push(folder)
      }
      children = folder.children!
    }
    children.push({
      id: tagKey(tag), label: structuredPath.tagName,
      path: [...parts, structuredPath.tagName].join('.'), sourceSegments: [...parts], folder: false, tag,
    })
  }
  const sort = (nodes: BrowseTreeNode[]) => {
    nodes.sort((a, b) => Number(b.folder) - Number(a.folder)
      || a.label.localeCompare(b.label, undefined, { numeric: true, sensitivity: 'base' }))
    for (const node of nodes) if (node.children) sort(node.children)
  }
  sort(roots)
  return roots
}

const fullTree = computed(() => buildTree(tags.value))
function filterTree(nodes: BrowseTreeNode[], needle: string): BrowseTreeNode[] {
  if (!needle) return nodes
  const result: BrowseTreeNode[] = []
  for (const node of nodes) {
    const children = node.children ? filterTree(node.children, needle) : []
    const haystack = `${node.label} ${node.path} ${node.tag?.address || ''}`.toLowerCase()
    if (haystack.includes(needle) || children.length)
      result.push({ ...node, children: node.folder ? children : undefined })
  }
  return result
}
const treeData = computed(() => filterTree(fullTree.value, query.value.trim().toLowerCase()))
const tagByKey = computed(() => new Map(tags.value.map((tag) => [tagKey(tag), tag])))
const legacyRawSelectedTags = computed(() => checkedKeys.value
  .map((key) => tagByKey.value.get(key)).filter((tag): tag is OpcUaBrowsedTag => !!tag))
const rawSelectedTags = computed(() => opcUaPreview.value ? preparedRawTags.value : legacyRawSelectedTags.value)
const selectedDeviceRoots = computed(() => opcUaPreview.value ? preparedDeviceRoots.value : [])
const sourceDeviceName = computed(() => opcUaPreview.value ? preparedSourceDeviceName.value : '')
const legacySelectedTags = computed(() => legacyRawSelectedTags.value
  .map((tag) => toImportedOpcUaTag(tag, aliases.value, sourceDeviceName.value)))
const selectedTags = computed(() => opcUaPreview.value ? preparedSelectedTags.value : legacySelectedTags.value)
const preview = computed(() => opcUaPreview.value
  ? preparedPreview.value
  : buildImportPreview(props.target || { tags: [], groups: [] }, selectedTags.value))
const blockingConflicts = computed(() => preview.value.conflicts.filter((item) => item.kind === 'group'))
const deviceRootError = computed(() => {
  if (!opcUaPreview.value || !rawSelectedTags.value.length) return ''
  if (selectedDeviceRoots.value.length > 1) return tr('一次只能导入一个设备，请只勾选一个顶层设备')
  if (!targetDeviceName.value.trim()) return tr('设备名称不能为空')
  if (targetDeviceName.value.includes('.')) return tr('设备名称不能包含“.”')
  return preparedTargetError.value
})
const canApply = computed(() =>
  !loading.value && !selectionLoading.value && !error.value && !preparationError.value && !anyTruncated.value && preview.value.tags.length > 0 && blockingConflicts.value.length === 0
  && !Object.values(branchErrors.value).some(Boolean)
  && aliasErrors.value.length === 0 && !deviceRootError.value
  && (preview.value.conflicts.length === 0 || overwriteConflicts.value))

function scheduleOpcUaPreparation() {
  if (!opcUaPreview.value) return
  const generation = ++preparationGeneration
  const activeBrowseGeneration = browseGeneration
  clearPreparedImport()
  const branchSnapshot: Record<string, OpcUaBrowsedTag[]> = {}
  for (const id of checkedKeys.value) {
    const branch = selectedBranches.value[id]
    if (branch) branchSnapshot[id] = branch
  }
  if (!Object.values(branchSnapshot).some(branch => branch.length > 0)) {
    preparingImport.value = false
    return
  }
  preparingImport.value = true
  const aliasSnapshot: OpcUaImportAliases = {
    rootGroupName: rootGroupName.value,
    folderNames: { ...folderNames.value },
    tagNames: { ...tagNames.value },
  }
  void prepareOpcUaImport({
    selectedBranches: branchSnapshot,
    selectedNodes: Object.fromEntries(checkedKeys.value.flatMap(id =>
      selectedBranchNodes.value[id] ? [[id, selectedBranchNodes.value[id]]] : [])),
    aliases: aliasSnapshot,
    target: props.target || { tags: [], groups: [] },
  }, {
    isCancelled: () => generation !== preparationGeneration
      || activeBrowseGeneration !== browseGeneration || !props.modelValue,
  }).then((result) => {
    if (!result || generation !== preparationGeneration
      || activeBrowseGeneration !== browseGeneration || !props.modelValue) return
    preparedRawTags.value = result.rawTags
    preparedDeviceRoots.value = result.sourceDeviceRoots
    preparedSourceDeviceName.value = result.sourceDeviceName
    preparedSelectedTags.value = result.tags
    preparedPreview.value = result.preview
    preparedTargetError.value = result.targetError || ''
    for (const node of Object.values(selectedBranchNodes.value)) {
      const exact = result.selectedRootTags[node.id]
      if (exact) {
        (node as BrowseTreeNode).tag = exact
        node.folder = false
      } else if (selectedBranches.value[node.id]?.length) node.folder = true
    }
  }).catch((err: any) => {
    if (generation === preparationGeneration && activeBrowseGeneration === browseGeneration && props.modelValue)
      preparationError.value = err?.message || tr('整理导入预览失败')
  }).finally(() => {
    if (generation === preparationGeneration) preparingImport.value = false
  })
}

function nodePathSegments(node: Partial<BrowseTreeNode> | null | undefined) {
  return normalizeOpcUaPathSegments(node?.sourceSegments, node?.label || '')
}
function nodeTagAliasKey(node: BrowseTreeNode) {
  const address = node.tag?.address || node.nodeId || ''
  return opcUaTagAliasKey({
    address,
    name: node.tag?.name || node.label,
    path: node.tag?.path || node.path,
    pathSegments: node.tag?.pathSegments || nodePathSegments(node),
  })
}
const selectedNodeIsDevice = computed(() => opcUaPreview.value && selectedNode.value?.folder
  && nodePathSegments(selectedNode.value).length === 1)
function defaultTargetDeviceName(root = sourceDeviceName.value) {
  const current = String(props.target?.name || '').trim()
  return !current || current.toLocaleLowerCase() === 'device' || current === '设备' ? (root || current) : current
}
const targetName = computed({
  get() {
    const node = selectedNode.value
    if (!node) return ''
    if (selectedNodeIsDevice.value) return targetDeviceName.value
    if (node.folder) return folderNames.value[folderAliasKey(nodePathSegments(node))] ?? defaultOpcUaGroupName(node.label)
    const address = node.tag?.address || node.nodeId
    return address ? (tagNames.value[nodeTagAliasKey(node)] ?? tagNames.value[address] ?? node.label) : node.label
  },
  set(value: string) {
    const node = selectedNode.value
    if (!node) return
    if (selectedNodeIsDevice.value) {
      targetDeviceName.value = value
      targetDeviceEdited.value = true
    } else if (node.folder) {
      folderNames.value[folderAliasKey(nodePathSegments(node))] = value
      scheduleOpcUaPreparation()
    }
    else {
      const address = node.tag?.address || node.nodeId
      if (address) {
        tagNames.value[nodeTagAliasKey(node)] = value
        scheduleOpcUaPreparation()
      }
    }
  },
})

function selectTargetNode(node: BrowseTreeNode) {
  selectedNode.value = !opcUaPreview.value || checkedKeys.value.includes(node.id) ? node : null
}
function resetTargetName() {
  const node = selectedNode.value
  if (!node) return
  if (selectedNodeIsDevice.value) {
    targetDeviceEdited.value = false
    targetDeviceName.value = defaultTargetDeviceName()
  } else if (node.folder) {
    delete folderNames.value[folderAliasKey(nodePathSegments(node))]
    scheduleOpcUaPreparation()
  }
  else {
    const address = node.tag?.address || node.nodeId
    if (address) {
      delete tagNames.value[nodeTagAliasKey(node)]
      scheduleOpcUaPreparation()
    }
  }
}
function displayedTargetName(node: BrowseTreeNode) {
  if (opcUaPreview.value && node.folder && nodePathSegments(node).length === 1) return targetDeviceName.value || node.label
  if (node.folder) return folderNames.value[folderAliasKey(nodePathSegments(node))] ?? defaultOpcUaGroupName(node.label)
  const address = node.tag?.address || node.nodeId
  return address ? (tagNames.value[nodeTagAliasKey(node)] ?? tagNames.value[address] ?? node.label) : node.label
}

function syncTreeChecks() {
  nextTick(() => treeRef.value?.setCheckedKeys?.(checkedKeys.value))
}
function onCheck(_data: BrowseTreeNode, info: any) {
  const keys = Array.isArray(info?.checkedKeys) ? info.checkedKeys : treeRef.value?.getCheckedKeys?.() || []
  checkedKeys.value = keys.filter((key: string) => tagByKey.value.has(key))
}
async function onLazyCheck(data: OpcUaLazyTreeNode, info: any) {
  const eventKeys = (Array.isArray(info?.checkedKeys) ? info.checkedKeys : []) as string[]
  const checked = eventKeys.includes(data.id)
  const selected = new Set(checkedKeys.value)
  if (!checked) {
    selected.delete(data.id)
    checkedKeys.value = [...selected]
    delete selectedBranches.value[data.id]
    delete branchIssues.value[data.id]
    delete truncatedBranches.value[data.id]
    delete selectedBranchNodes.value[data.id]
    delete branchErrors.value[data.id]
    scheduleOpcUaPreparation()
    if (selectedNode.value?.id === data.id) selectedNode.value = null
    return
  }
  selected.add(data.id)
  checkedKeys.value = [...selected]
  selectedBranchNodes.value[data.id] = data
  selectedNode.value = data
  if (selectedBranches.value[data.id] || branchLoadingKeys.value.includes(data.id)) return
  await loadSelectedBranch(data)
}
async function loadSelectedBranch(data: OpcUaLazyTreeNode) {
  const generation = browseGeneration
  const operation = beginBrowseOperation('扫描所选分支', data.path || data.label)
  branchLoadingKeys.value = [...branchLoadingKeys.value, data.id]
  error.value = ''
  delete branchErrors.value[data.id]
  delete selectedBranches.value[data.id]
  delete branchIssues.value[data.id]
  delete truncatedBranches.value[data.id]
  scheduleOpcUaPreparation()
  try {
    const result = await api.browseOpcUaBranch({
      connection: { ...props.connection },
      nodeId: data.nodeId,
      name: data.label,
      isVariable: data.isVariable,
      pathSegments: nodePathSegments(data),
      limit: 100_000,
    })
    if (generation !== browseGeneration || !props.modelValue) return
    finishBrowseOperation(operation, false, result)
    if (!checkedKeys.value.includes(data.id)) return
    selectedBranches.value[data.id] = markRaw(result.tags || [])
    branchIssues.value[data.id] = markRaw(result.issues || [])
    truncatedBranches.value[data.id] = !!result.truncated
    scheduleOpcUaPreparation()
    if ((result.tags || []).length && !data.isVariable) data.folder = true
    if (!result.tags?.length && !result.truncated && !result.issues?.length) {
      branchErrors.value[data.id] = tr('所选节点下未发现可导入变量')
      ElMessage.warning(branchErrors.value[data.id])
    }
  } catch (err: any) {
    if (generation !== browseGeneration || !props.modelValue) return
    finishBrowseOperation(operation, true, err?.response?.data)
    if (checkedKeys.value.includes(data.id))
      branchErrors.value[data.id] = err?.response?.data?.error || err?.message || tr('所选分支浏览失败')
  } finally {
    if (generation === browseGeneration)
      branchLoadingKeys.value = branchLoadingKeys.value.filter(key => key !== data.id)
  }
}
async function loadLazyChildren(node: any, resolve: (children: OpcUaLazyTreeNode[]) => void) {
  if (isLazyBrowseRootNode(node)) { resolve(lazyTree.value); return }
  const raw = node?.data
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) { resolve([]); return }
  const data = raw as OpcUaLazyTreeNode
  const generation = browseGeneration
  const operation = beginBrowseOperation('展开节点', data.path || data.label)
  try {
    const result = await api.browseOpcUaChildren({
      connection: { ...props.connection },
      nodeId: data.nodeId,
      name: data.label,
      isVariable: data.isVariable,
      pathSegments: nodePathSegments(data),
    })
    if (generation !== browseGeneration || !props.modelValue) { resolve([]); return }
    finishBrowseOperation(operation, false, result)
    const children = (result.nodes || []).map(toLazyBrowseTreeNode)
    if (children.length) data.folder = true
    else data.isLeaf = true
    resolve(children)
    syncTreeChecks()
  } catch (err: any) {
    if (generation !== browseGeneration || !props.modelValue) { resolve([]); return }
    finishBrowseOperation(operation, true, err?.response?.data)
    error.value = err?.response?.data?.error || err?.message || tr('展开节点失败')
    resolve([])
  }
}
function removeSelectedBranch(id: string) {
  checkedKeys.value = checkedKeys.value.filter(key => key !== id)
  delete selectedBranches.value[id]
  delete branchIssues.value[id]
  delete truncatedBranches.value[id]
  delete selectedBranchNodes.value[id]
  delete branchErrors.value[id]
  scheduleOpcUaPreparation()
  if (selectedNode.value?.id === id) selectedNode.value = null
  treeRef.value?.setChecked?.(id, false, false)
}
function retrySelectedBranch(id: string) {
  const node = selectedBranchNodes.value[id]
  if (node && !branchLoadingKeys.value.includes(id)) loadSelectedBranch(node)
}
async function retryBrowseIssue(branchId: string, issue: OpcUaBrowseIssue) {
  const generation = browseGeneration
  const key = browseIssueKey(branchId, issue)
  if (issueRetries.value.length) return
  const originalIssues = branchIssues.value[branchId]
  issueRetries.value.push(key)
  try {
    const result = await api.browseOpcUaBranch({ connection: { ...props.connection },
      nodeId: issue.nodeId, name: issue.name, isVariable: true,
      pathSegments: issue.pathSegments, limit: 100_000 })
    if (generation !== browseGeneration || !props.modelValue) return
    if (!checkedKeys.value.includes(branchId) || branchIssues.value[branchId] !== originalIssues) return
    if (result.truncated) throw new Error(tr('重试结果超出上限，请缩小范围'))
    if (!result.tags?.length && !result.issues?.length) throw new Error(tr('未发现可导入变量'))
    selectedBranches.value[branchId] = markRaw(flattenSelectedBranchTags({
      previous: selectedBranches.value[branchId] || [], retry: result.tags || [],
    }))
    scheduleOpcUaPreparation()
    branchIssues.value[branchId] = markRaw(originalIssues.filter(item => !sameBrowseIssue(item, issue)).concat(result.issues || []))
  } catch (err: any) {
    if (generation !== browseGeneration || !props.modelValue) return
    if (branchIssues.value[branchId] === originalIssues)
      branchIssues.value[branchId] = markRaw(originalIssues.map(item => sameBrowseIssue(item, issue)
        ? { ...item, reason: err?.response?.data?.error || err?.message || tr('节点重试失败') } : item))
    ElMessage.error(err?.response?.data?.error || err?.message || tr('节点重试失败'))
  } finally {
    if (generation === browseGeneration) issueRetries.value = issueRetries.value.filter(item => item !== key)
  }
}
function filterLazyNode(value: string, data: OpcUaLazyTreeNode) {
  const needle = value.trim().toLowerCase()
  return !needle || `${data.label} ${data.path} ${data.nodeId}`.toLowerCase().includes(needle)
}
function clearSelection() {
  checkedKeys.value = []
  selectedBranches.value = {}
  branchIssues.value = {}
  truncatedBranches.value = {}
  selectedBranchNodes.value = {}
  branchErrors.value = {}
  selectedNode.value = null
  overwriteConflicts.value = false
  scheduleOpcUaPreparation()
  syncTreeChecks()
}
function selectAll() {
  checkedKeys.value = tags.value.map(tagKey)
  syncTreeChecks()
}

async function load() {
  const generation = ++browseGeneration
  cancelPreparation()
  clearPreparedImport()
  stopElapsedTimer()
  browseOperations.value = []
  const operation = beginBrowseOperation(props.deviceName ? '扫描设备标签' : '连接并读取根节点', props.deviceName || props.connection.url)
  loading.value = true
  error.value = ''
  truncated.value = false
  tags.value = []
  lazyTree.value = []
  selectedBranches.value = {}
  branchIssues.value = {}
  truncatedBranches.value = {}
  selectedBranchNodes.value = {}
  branchErrors.value = {}
  branchLoadingKeys.value = []
  issueRetries.value = []
  issuePage.value = 1
  rootGroupName.value = ''
  folderNames.value = {}
  tagNames.value = {}
  selectedNode.value = null
  targetDeviceEdited.value = false
  targetDeviceName.value = String(props.target?.name || '').trim()
  clearSelection()
  try {
    if (props.deviceName) {
      const browsedTags = await api.browse(props.deviceName)
      if (generation !== browseGeneration || !props.modelValue) return
      tags.value = browsedTags
      finishBrowseOperation(operation, false, { tags: tags.value })
      truncated.value = false
    } else {
      const result = await api.browseOpcUaChildren({
        connection: { ...props.connection }, nodeId: '', pathSegments: [],
      })
      if (generation !== browseGeneration || !props.modelValue) return
      finishBrowseOperation(operation, false, result)
      lazyTree.value = (result.nodes || []).map(toLazyBrowseTreeNode)
      truncated.value = false
    }
    if (!(props.deviceName ? tags.value.length : lazyTree.value.length) && !truncated.value)
      ElMessage.warning(tr('服务器地址空间中未发现可浏览节点'))
  } catch (err: any) {
    if (generation !== browseGeneration || !props.modelValue) return
    finishBrowseOperation(operation, true, err?.response?.data)
    error.value = err?.response?.data?.error || err?.message || tr('连接或浏览失败')
  } finally {
    if (generation === browseGeneration) loading.value = false
  }
}

watch(issuePageCount, pages => { issuePage.value = Math.min(issuePage.value, pages) }, { flush: 'sync' })
const browseContextKey = computed(() => JSON.stringify([props.deviceName || '', props.connection]))
watch([() => props.modelValue, browseContextKey], ([open, context], [wasOpen, previousContext]) => {
  if (!open) { cancelBrowseContext(); return }
  if (!wasOpen || context !== previousContext) {
    query.value = ''
    load()
  }
})
watch(() => props.target, () => {
  if (props.modelValue) scheduleOpcUaPreparation()
})
watch(treeData, syncTreeChecks)
watch(query, (value) => {
  if (opcUaPreview.value) treeRef.value?.filter?.(value)
})
watch(sourceDeviceName, (root) => {
  if (opcUaPreview.value && !targetDeviceEdited.value) targetDeviceName.value = defaultTargetDeviceName(root)
})

function close() {
  cancelBrowseContext()
  emit('update:modelValue', false)
}
function apply() {
  if (!canApply.value || !props.editable) return
  emit('apply', {
    tags: selectedTags.value,
    overwriteConflicts: overwriteConflicts.value,
    sourceDeviceName: opcUaPreview.value ? sourceDeviceName.value : undefined,
    targetDeviceName: opcUaPreview.value ? targetDeviceName.value.trim() : undefined,
  })
  close()
}
</script>

<template>
  <el-dialog :model-value="modelValue" :title="$t(deviceName ? '自动生成标记 — 浏览 Siemens PLC' : '自动生成标记 — 浏览 OPC UA 服务器')" width="980px" top="5vh"
    append-to-body destroy-on-close :close-on-click-modal="false" @close="close">
    <div class="browser-head">
      <div class="endpoint">
        <span>{{ $t(deviceName ? 'PLC 设备' : 'UA 服务器') }}</span><strong :title="deviceName || connection.url">{{ deviceName || connection.url }}</strong>
      </div>
      <el-button :loading="loading" @click="load">{{ $t('重新连接') }}</el-button>
    </div>

    <div v-if="issueCount" class="browse-issues">
      <strong>{{ tr('类型解析失败 {count} 项；正常标签可继续导入', { count: issueCount }) }}</strong>
      <div class="browse-issue-list">
      <div v-for="issue in visibleIssues" :key="issue.issueKey" class="browse-issue-row">
        <code>{{ issue.nodeId }}</code> · {{ issue.reason }}
        <el-button text type="primary" :disabled="issueRetries.length > 0" :loading="issueRetries.includes(issue.issueKey)"
          @click="retryBrowseIssue(issue.branchId, issue)">{{ tr('单独重试') }}</el-button>
      </div>
      </div>
      <div class="browse-issues-pagination">
        <span>{{ tr('第 {page} / {pages} 页 · 每页 {size} 项', { page: issuePage, pages: issuePageCount, size: issuePageSize }) }}</span>
        <el-button text :disabled="issuePage <= 1" @click="issuePage--">{{ tr('上一页') }}</el-button>
        <el-button text :disabled="issuePage >= issuePageCount" @click="issuePage++">{{ tr('下一页') }}</el-button>
      </div>
    </div>
    <el-alert v-if="anyTruncated" type="error" :closable="false" show-icon
      :title="$t('所选分支超过浏览数量或深度上限，当前结果不能导入，请选择更具体的结构或数据块。')" />
    <el-alert v-else-if="error || preparationError" type="error" :closable="false" show-icon :title="error || preparationError" />

    <div v-if="browseOperations.length" class="browse-progress" role="status" aria-live="polite">
      <div class="browse-progress-summary">
        <strong>{{ $t('浏览进度') }}</strong>
        <span>{{ $t('已完成分支') }} {{ completedBranchCount }} · {{ $t('已发现标签（已返回结果）') }} {{ opcUaPreview ? rawSelectedTags.length : tags.length }}</span>
      </div>
      <div v-for="operation in visibleOperations" :key="operation.id" class="browse-operation">
        <span class="browse-operation-label" :title="operation.label">{{ $t(operation.stage) }} · {{ operation.label }}</span>
        <strong :class="{ 'range-error': operation.failed }">{{ $t(operation.finishedAt === undefined ? '进行中' : operation.failed ? '失败' : '完成') }} · {{ browseOperationElapsed(operation, operationClock) }}</strong>
        <small v-if="operation.scannedNodeCount !== undefined">{{ $t('扫描节点') }} {{ operation.scannedNodeCount }}</small>
        <small v-if="operation.discoveredTagCount !== undefined">{{ $t('发现标签') }} {{ operation.discoveredTagCount }}</small>
        <small v-if="operation.serverElapsedMilliseconds !== undefined">{{ $t('服务器浏览耗时') }} {{ operation.serverElapsedMilliseconds }} ms</small>
      </div>
      <small class="browse-progress-hint">{{ $t('时间为本次请求总耗时，含排队、会话建立或复用及浏览，不代表 PLC 采集耗时；扫描计数在服务器返回后显示。') }}</small>
    </div>

    <div class="browser-tools">
      <el-input v-model="query" clearable :placeholder="$t('搜索名称、路径或 NodeId…')" />
      <span v-if="opcUaPreview">{{ $t('按需展开，不再全量扫描') }}</span>
      <span v-else>{{ $t('发现 {count} 个', { count: tags.length }) }}</span>
      <strong v-if="opcUaPreview">{{ $t('已选 {count} 个范围 · {tagCount} 点', { count: selectedBranchRows.length, tagCount: selectedTags.length }) }}</strong>
      <strong v-else>{{ $t('已选 {count} 个', { count: selectedTags.length }) }}</strong>
      <el-button v-if="!opcUaPreview" text :disabled="!tags.length" @click="selectAll">{{ $t('全选') }}</el-button>
      <el-button text :disabled="opcUaPreview ? !checkedKeys.length : !selectedTags.length" @click="clearSelection">{{ $t('清空选择') }}</el-button>
    </div>

    <div v-loading="loading" class="browser-body"
      :element-loading-text="$t('正在连接 OPC UA 服务器…')">
      <div class="tree-pane">
        <el-empty v-if="!loading && !(opcUaPreview ? lazyTree.length : treeData.length)" :description="$t('没有匹配的变量')" :image-size="60" />
        <el-tree v-else-if="opcUaPreview" ref="treeRef" class="lazy-tree" :data="lazyTree" :props="lazyTreeProps"
          node-key="id" lazy :load="loadLazyChildren" show-checkbox check-strictly :filter-node-method="filterLazyNode"
          :default-checked-keys="checkedKeys" @check="onLazyCheck" @node-click="selectTargetNode">
          <template #default="{ data }">
            <div class="tree-line" :class="{ variable: !data.folder }">
              <span class="kind">{{ data.folder ? '▸' : '●' }}</span>
              <span class="label" :title="`${data.path}\n${data.nodeId}`">{{ data.label }}</span>
              <em v-if="displayedTargetName(data) !== data.label" :title="$t('导入为 {name}', { name: displayedTargetName(data) })">→ {{ displayedTargetName(data) }}</em>
              <small v-if="branchLoadingKeys.includes(data.id)">{{ $t('读取中…') }}</small>
            </div>
          </template>
        </el-tree>
        <el-tree-v2 v-else ref="treeRef" :data="treeData" :props="treeProps" :height="450" :item-size="34"
          show-checkbox :default-checked-keys="checkedKeys" @check="onCheck" @node-click="selectTargetNode">
          <template #default="{ data }">
            <div class="tree-line" :class="{ variable: !data.folder }">
              <span class="kind">{{ data.folder ? '▸' : '●' }}</span>
              <span class="label" :title="data.path">{{ data.label }}</span>
              <em v-if="displayedTargetName(data) !== data.label" :title="$t('导入为 {name}', { name: displayedTargetName(data) })">→ {{ displayedTargetName(data) }}</em>
              <small v-if="data.tag">{{ dataTypeName(data.tag.dataType) }}{{ data.tag.isArray ? '[]' : '' }}</small>
            </div>
          </template>
        </el-tree-v2>
      </div>

      <div class="selection-pane">
        <div class="selection-title">
          <strong>{{ $t('导入预览') }}</strong>
          <span>{{ $t('{count} 个新组', { count: preview.groupsToCreate }) }}<span v-if="preview.skipped"> · {{ $t('已存在跳过 {count} 个', { count: preview.skipped }) }}</span></span>
        </div>
        <div v-if="opcUaPreview && selectedBranchRows.length" class="selected-ranges">
          <div class="selected-ranges-head">
            <strong>{{ $t('已选范围') }}</strong>
            <span>{{ $t('可逐项撤销') }}</span>
          </div>
          <div class="selected-ranges-scroll">
            <div v-for="row in selectedBranchRows" :key="row.id" class="selected-range-row" :class="`is-${row.status}`">
              <div class="selected-range-main">
                <strong :title="row.path">{{ row.label }}</strong>
                <div class="selected-range-path">
                  <span>{{ $t('实际路径') }}</span>
                  <code>{{ row.path }}</code>
                </div>
              </div>
              <span v-if="row.status === 'loading'">{{ $t('读取中…') }}</span>
              <span v-else-if="row.status === 'ready'">{{ $t('{count} 点', { count: row.tagCount }) }}</span>
              <span v-else-if="row.status === 'truncated'" class="range-error">{{ $t('范围过大') }}</span>
              <el-button v-else text type="danger" @click="retrySelectedBranch(row.id)">{{ $t('重试') }}</el-button>
              <el-button text type="danger" @click="removeSelectedBranch(row.id)">{{ $t('移除') }}</el-button>
              <small v-if="row.error" class="range-error" :title="row.error">{{ row.error }}</small>
            </div>
          </div>
        </div>
        <div class="rename-editor">
          <div class="rename-head"><strong>{{ $t('目标名称') }}</strong><span>{{ $t('选择左侧设备、结构或点位可修改导入名称') }}</span></div>
          <el-input v-if="!opcUaPreview" v-model="rootGroupName" :disabled="!editable" clearable :placeholder="$t('可选：统一导入到新根组')" class="root-name">
            <template #prepend>{{ $t('根组') }}</template>
          </el-input>
          <template v-if="selectedNode">
            <div class="source-name"><span>{{ $t(selectedNodeIsDevice ? '设备' : selectedNode.folder ? '结构' : '点位') }}</span><code>{{ selectedNode.path }}</code></div>
            <div class="target-input">
              <el-input v-model="targetName" :disabled="!editable" :placeholder="$t('输入导入后的名称')" />
              <el-button :disabled="!editable" @click="resetTargetName">{{ $t('恢复原名') }}</el-button>
            </div>
          </template>
          <div v-else class="rename-empty">{{ $t('当前未选择要重命名的节点；默认按服务器原名导入。') }}</div>
          <div v-if="aliasErrors.length" class="alias-errors">{{ aliasErrors.join('；') }}</div>
          <div v-if="deviceRootError" class="alias-errors">{{ deviceRootError }}</div>
          <div class="rename-hint">{{ $t('修改结构名称会自动作用于其全部后代；NodeId 和读取地址不会改变。') }}</div>
        </div>
        <div v-if="preparingImport" class="import-preparing" role="status">{{ $t('正在整理完整导入预览…') }}</div>
        <div v-else-if="selectedTags.length" class="selected-scroll">
          <div v-for="tag in selectedTags.slice(0, 300)" :key="`${tag.address}\u0000${tag.groupPath.join('\u0000')}\u0000${tag.name}`" class="selected-row">
            <strong :title="[...tag.groupPath, tag.name].join('.')">{{ [...tag.groupPath, tag.name].join('.') }}</strong>
            <span>{{ tag.dataType }}{{ tag.isArray ? '[]' : '' }}</span>
            <small :title="tag.address">{{ tag.address }}</small>
          </div>
          <p v-if="selectedTags.length > 300" class="more">{{ $t('其余 {count} 个已选择，导入时会一并处理。', { count: selectedTags.length - 300 }) }}</p>
        </div>
        <el-empty v-else :description="$t('在左侧选择要导入的变量')" :image-size="58" />

        <div v-if="preview.conflicts.length" class="conflicts">
          <strong>{{ blockingConflicts.length ? `发现 ${blockingConflicts.length} 个结构冲突，无法直接导入` : `发现 ${preview.conflicts.length} 个同路径冲突` }}</strong>
          <span>{{ preview.conflicts.slice(0, 3).map((item) => item.path).join('、') }}</span>
          <el-checkbox v-if="!blockingConflicts.length" v-model="overwriteConflicts" :disabled="!editable">{{ $t('覆盖同路径标记') }}</el-checkbox>
        </div>
      </div>
    </div>

    <template #footer>
      <span class="footer-note">{{ $t(opcUaPreview ? '勾选结构会读取并导入该分支；关闭窗口不会修改工程。' : '只导入已勾选变量；关闭窗口不会修改工程。') }}</span>
      <el-button @click="close">{{ $t(editable ? '取消' : '关闭') }}</el-button>
      <el-button v-if="editable" type="primary" :loading="selectionLoading" :disabled="!canApply" @click="apply">{{ $t('导入 {count} 个', { count: preview.tags.length }) }}<span v-if="preview.skipped">{{ $t('（跳过 {count}）', { count: preview.skipped }) }}</span></el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.browse-issues { padding:10px; background:#fff8e8; color:#805800; overflow-wrap:anywhere; }
.browse-issue-list { max-height:150px; overflow:auto; }
.browse-issue-row { padding:3px 0; }
.browse-issues-pagination { display:flex; align-items:center; gap:10px; padding-top:4px; }
.browse-progress { margin:10px 0; padding:10px 12px; background:#f3f7fc; border:1px solid #dce4ee; border-radius:8px; font-size:12px; }
.browse-progress-summary,.browse-operation { display:flex; align-items:center; flex-wrap:wrap; gap:8px 14px; margin-bottom:4px; }
.browse-operation-label { max-width:440px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.browse-progress-hint { color:#62748a; }
.import-preparing { padding:24px 12px; text-align:center; color:#62748a; }
.browser-head,.browser-tools,.selection-title { display:flex; align-items:center; gap:10px; }
.browser-head { justify-content:space-between; margin-bottom:10px; }
.endpoint { display:flex; align-items:center; min-width:0; gap:9px; color:#7a8796; }
.endpoint strong { color:#26384b; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.browser-tools { margin:12px 0 8px; color:#7a8796; font-size:12px; }
.browser-tools .el-input { width:390px; margin-right:auto; }
.browser-tools strong { color:#2472bd; }
.browser-body { display:grid; grid-template-columns:minmax(0, 1.15fr) minmax(320px, .85fr); height:452px; border:1px solid #dce4ee; border-radius:9px; overflow:hidden; }
.tree-pane { min-width:0; border-right:1px solid #e2e8f0; overflow:auto; }
.lazy-tree { min-width:100%; padding:4px 0; }
.lazy-tree :deep(.el-tree-node__content) { height:34px; }
.tree-line { display:flex; align-items:center; min-width:0; width:100%; gap:7px; }
.tree-line .kind { color:#7b8da1; width:14px; }
.tree-line.variable .kind { color:#409eff; font-size:8px; }
.tree-line .label { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.tree-line em { color:#2472bd; font-size:11px; font-style:normal; max-width:120px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.tree-line small { margin-left:auto; color:#8391a1; padding-right:10px; }
.selection-pane { display:flex; flex-direction:column; min-width:0; min-height:0; overflow:hidden; background:#fafbfd; }
.selection-title { height:42px; padding:0 12px; border-bottom:1px solid #e6ebf1; }
.selection-title span { margin-left:auto; color:#7d8997; font-size:12px; }
.selected-ranges { flex:0 0 auto; max-height:142px; border-bottom:1px solid #e1e7ef; background:#fff; display:flex; flex-direction:column; min-height:0; }
.selected-ranges-head { display:flex; align-items:center; gap:8px; padding:7px 10px 4px; font-size:12px; }
.selected-ranges-head span { margin-left:auto; color:#8794a3; font-size:11px; }
.selected-ranges-scroll { overflow:auto; padding:2px 8px 7px; }
.selected-range-row { display:grid; grid-template-columns:minmax(0,1fr) auto auto; align-items:center; gap:5px; padding:6px 7px; margin-top:4px; border:1px solid #dfe7f0; border-radius:6px; background:#f8fbff; }
.selected-range-row.is-loading { border-color:#b8d7f5; }
.selected-range-row.is-error,.selected-range-row.is-truncated { border-color:#f0b8b2; background:#fff7f5; }
.selected-range-main { min-width:0; display:grid; gap:1px; }
.selected-range-main strong,.selected-range-row > .range-error { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.selected-range-path { display:grid; grid-template-columns:auto minmax(0,1fr); align-items:start; gap:5px; min-width:0; color:#8190a1; font-size:11px; }
.selected-range-path span { flex-shrink:0; color:#2472bd; }
.selected-range-path code { min-width:0; color:#65768a; line-height:1.35; white-space:normal; overflow-wrap:anywhere; word-break:break-word; }
.selected-range-row > span { color:#357bbd; font-size:11px; }
.selected-range-row > .range-error { grid-column:1 / -1; color:#c34236; }
.rename-editor { padding:9px 10px; border-bottom:1px solid #e1e7ef; background:#f5f9ff; display:grid; gap:7px; }
.rename-head,.source-name,.target-input { display:flex; align-items:center; gap:8px; min-width:0; }
.rename-head span,.rename-empty,.rename-hint { color:#7d8997; font-size:11px; }
.rename-head span { margin-left:auto; }
.root-name { width:100%; }
.source-name span { color:#2472bd; font-size:11px; flex-shrink:0; }
.source-name { align-items:flex-start; }
.source-name code { min-width:0; white-space:normal; overflow-wrap:anywhere; word-break:break-word; color:#536579; line-height:1.4; }
.target-input .el-input { min-width:0; }
.target-input .el-button { margin-left:auto; flex-shrink:0; }
.alias-errors { color:#c0392b; font-size:12px; }
.selected-scroll { flex:1; min-height:0; overflow:auto; padding:8px; }
.selected-row { display:grid; grid-template-columns:minmax(0,1fr) auto; gap:3px 9px; padding:7px 8px; background:white; border:1px solid #e3e9f0; border-radius:6px; margin-bottom:6px; }
.selected-row strong,.selected-row small { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.selected-row strong { font-size:12px; color:#33475b; }
.selected-row span { font-size:11px; color:#2775bd; }
.selected-row small { grid-column:1 / -1; color:#8a96a5; }
.conflicts { padding:9px 11px; border-top:1px solid #f0c7c3; background:#fff5f3; display:grid; gap:4px; color:#bf3c30; font-size:12px; }
.conflicts span { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; color:#8d5c57; }
.more,.footer-note { color:#8794a3; font-size:12px; }
.footer-note { float:left; line-height:32px; }
</style>
