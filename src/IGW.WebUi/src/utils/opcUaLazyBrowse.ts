import type { OpcUaBrowsedNode, OpcUaBrowsedTag } from '../api'
import { createEmptyImportPreviewAccumulator, type ImportedTag, type TagImportPreview } from './kepTagImport'
import {
  opcUaTagAliasKey, structuredOpcUaTagPath,
  toImportedOpcUaTag, type OpcUaImportAliases,
} from './opcUaStructuredImport'

export interface OpcUaLazyTreeNode {
  id: string
  label: string
  path: string
  sourceSegments: string[]
  folder: boolean
  isLeaf: boolean
  nodeId: string
  isVariable: boolean
  children?: OpcUaLazyTreeNode[]
}

export type SelectedBranchStatus = 'loading' | 'ready' | 'error' | 'truncated'

export interface SelectedBranchRow {
  id: string
  label: string
  path: string
  status: SelectedBranchStatus
  tagCount: number
  error: string
}

export interface OpcUaBrowseOperation {
  id: number
  stage: string
  label: string
  startedAt: number
  finishedAt?: number
  failed?: boolean
  scannedNodeCount?: number
  discoveredTagCount?: number
  serverElapsedMilliseconds?: number
}

export interface OpcUaImportPreparationInput {
  selectedBranches: Record<string, OpcUaBrowsedTag[]>
  selectedNodes?: Record<string, Pick<OpcUaLazyTreeNode, 'nodeId' | 'label' | 'sourceSegments'>>
  aliases: OpcUaImportAliases
  target: any
}

export interface OpcUaImportPreparationResult {
  rawTags: OpcUaBrowsedTag[]
  sourceDeviceRoots: string[]
  sourceDeviceName: string
  selectedRootTags: Record<string, OpcUaBrowsedTag>
  tags: ImportedTag[]
  preview: TagImportPreview
  targetError?: string
}

export interface OpcUaImportPreparationOptions {
  batchSize?: number
  frameBudgetMs?: number
  yieldControl?: () => Promise<void>
  isCancelled?: () => boolean
}

function yieldToBrowser(): Promise<void> {
  return new Promise(resolve => {
    if (typeof globalThis.requestAnimationFrame === 'function') {
      globalThis.requestAnimationFrame(() => resolve())
      return
    }
    setTimeout(resolve, 0)
  })
}

/**
 * Prepares a complete OPC UA selection while returning control to the browser
 * between bounded batches. A cancelled run never returns a partial preview.
 */
export async function prepareOpcUaImport(
  input: OpcUaImportPreparationInput,
  options: OpcUaImportPreparationOptions = {},
): Promise<OpcUaImportPreparationResult | null> {
  const batchSize = Math.max(1, Math.floor(options.batchSize || 2_000))
  const frameBudgetMs = Math.max(1, options.frameBudgetMs || 8)
  const yieldControl = options.yieldControl || yieldToBrowser
  const isCancelled = options.isCancelled || (() => false)
  let workSinceYield = 0
  let frameStartedAt = performance.now()
  const shouldYield = () => {
    workSinceYield++
    return workSinceYield >= batchSize
      || (workSinceYield % 32 === 0 && performance.now() - frameStartedAt >= frameBudgetMs)
  }
  const yieldAndContinue = async () => {
    workSinceYield = 0
    await yieldControl()
    frameStartedAt = performance.now()
    return !isCancelled()
  }

  const rawTags: OpcUaBrowsedTag[] = []
  const uniqueTags = new Set<string>()
  const selectedRootTags: Record<string, OpcUaBrowsedTag> = {}
  const sourceDeviceRoots: string[] = []
  const sourceRootKeys = new Set<string>()
  for (const [branchId, branch] of Object.entries(input.selectedBranches)) {
    const selectedNode = input.selectedNodes?.[branchId]
    const selectedPath = normalizeOpcUaPathSegments(selectedNode?.sourceSegments, selectedNode?.label).join('.')
    for (const tag of branch) {
      const structuredPath = structuredOpcUaTagPath(tag)
      if (!selectedRootTags[branchId] && selectedNode && tag.address === selectedNode.nodeId
        && [...structuredPath.folderSegments, structuredPath.tagName].join('.') === selectedPath)
        selectedRootTags[branchId] = tag
      const tagKey = opcUaTagAliasKey(tag)
      if (!uniqueTags.has(tagKey)) {
        uniqueTags.add(tagKey)
        rawTags.push(tag)
        const root = structuredPath.folderSegments[0]?.trim()
        const rootKey = root?.toLocaleLowerCase()
        if (root && rootKey && !sourceRootKeys.has(rootKey)) {
          sourceRootKeys.add(rootKey)
          sourceDeviceRoots.push(root)
        }
      }
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
    }
  }

  const sourceDeviceName = sourceDeviceRoots.length === 1 ? sourceDeviceRoots[0] : ''
  const tags: ImportedTag[] = []
  for (const tag of rawTags) {
    tags.push(toImportedOpcUaTag(tag, input.aliases, sourceDeviceName))
    if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
  }

  const previewAccumulator = createEmptyImportPreviewAccumulator()
  const target = input.target || { tags: [], groups: [] }
  const nameKey = (value: unknown) => String(value ?? '').trim().toLocaleLowerCase()
  const tagsOf = (container: any): any[] => Array.isArray(container?.tags) ? container.tags : []
  const groupsOf = (container: any): any[] => {
    const groups = container?.groups ?? container?.subGroups
    return Array.isArray(groups) ? groups : []
  }
  const indexContainer = async (container: any, parent: string[]): Promise<boolean> => {
    for (const tag of tagsOf(container)) {
      previewAccumulator.addExistingTag([...parent, String(tag?.name ?? '')], tag)
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return false
    }
    for (const group of groupsOf(container)) {
      const path = [...parent, String(group?.name ?? '')]
      previewAccumulator.addExistingGroup(path)
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return false
      if (!await indexContainer(group, path)) return false
    }
    return true
  }

  const rootGroupKey = 'groups' in target ? 'groups' : 'subGroups'
  const rootGroups = Array.isArray(target?.[rootGroupKey]) ? target[rootGroupKey] : []
  const rootTags = tagsOf(target)
  let legacyRootIndex = -1
  if (sourceDeviceName) {
    const sourceKey = nameKey(sourceDeviceName)
    for (let index = 0; index < rootGroups.length; index++) {
      if (legacyRootIndex < 0 && nameKey(rootGroups[index]?.name) === sourceKey) legacyRootIndex = index
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
    }
  }

  let targetError = ''
  let flattenLegacyRoot = legacyRootIndex >= 0
  const wrapper = flattenLegacyRoot ? rootGroups[legacyRootIndex] : undefined
  const wrapperGroups = Array.isArray(wrapper?.subGroups) ? wrapper.subGroups : []
  const wrapperTags = tagsOf(wrapper)
  if (flattenLegacyRoot) {
    const siblingGroupNames = new Set<string>()
    const targetTagNames = new Set<string>()
    const wrapperGroupNames = new Set<string>()
    const wrapperTagNames = new Set<string>()
    for (let index = 0; index < rootGroups.length; index++) {
      if (index !== legacyRootIndex) siblingGroupNames.add(nameKey(rootGroups[index]?.name))
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
    }
    for (const tag of rootTags) {
      targetTagNames.add(nameKey(tag?.name))
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
    }
    for (const group of wrapperGroups) {
      const key = nameKey(group?.name)
      if (key && (wrapperGroupNames.has(key) || siblingGroupNames.has(key) || targetTagNames.has(key))) {
        targetError = `无法展开设备根：目标中已存在同名结构「${group.name}」`
        break
      }
      if (key) wrapperGroupNames.add(key)
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
    }
    if (!targetError) {
      for (const tag of wrapperTags) {
        const key = nameKey(tag?.name)
        if (key && (wrapperTagNames.has(key) || targetTagNames.has(key)
          || siblingGroupNames.has(key) || wrapperGroupNames.has(key))) {
          targetError = `无法展开设备根：目标中已存在同名点位「${tag.name}」`
          break
        }
        if (key) wrapperTagNames.add(key)
        if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
      }
    }
    if (targetError) flattenLegacyRoot = false
  }

  if (flattenLegacyRoot) {
    for (const tag of rootTags) {
      previewAccumulator.addExistingTag([String(tag?.name ?? '')], tag)
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
    }
    for (const tag of wrapperTags) {
      previewAccumulator.addExistingTag([String(tag?.name ?? '')], tag)
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
    }
    for (let index = 0; index < rootGroups.length; index++) {
      if (index === legacyRootIndex) continue
      const group = rootGroups[index]
      const path = [String(group?.name ?? '')]
      previewAccumulator.addExistingGroup(path)
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
      if (!await indexContainer(group, path)) return null
    }
    for (const group of wrapperGroups) {
      const path = [String(group?.name ?? '')]
      previewAccumulator.addExistingGroup(path)
      if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
      if (!await indexContainer(group, path)) return null
    }
  } else if (!await indexContainer(target, [])) return null

  for (const tag of tags) {
    previewAccumulator.add(tag)
    if (isCancelled() || (shouldYield() && !await yieldAndContinue())) return null
  }

  return {
    rawTags,
    sourceDeviceRoots,
    sourceDeviceName,
    selectedRootTags,
    tags,
    preview: previewAccumulator.finish(),
    targetError: targetError || undefined,
  }
}

export function browseOperationElapsed(operation: OpcUaBrowseOperation, now: number): string {
  return `${(Math.max(0, (operation.finishedAt ?? now) - operation.startedAt) / 1000).toFixed(2)} 秒`
}

/** Counts are server evidence only; absence never means zero or a guessed completion percentage. */
export function browseResultMetrics(result: unknown): Pick<OpcUaBrowseOperation,
  'scannedNodeCount' | 'discoveredTagCount' | 'serverElapsedMilliseconds'> {
  if (!result || typeof result !== 'object') return {}
  const value = result as Record<string, unknown>
  const nonNegative = (item: unknown) => typeof item === 'number' && Number.isFinite(item) && item >= 0 ? item : undefined
  return {
    scannedNodeCount: nonNegative(value.scannedNodeCount),
    discoveredTagCount: Array.isArray(value.tags) ? value.tags.length : undefined,
    serverElapsedMilliseconds: nonNegative(value.elapsedMilliseconds),
  }
}

/** OPC UA 数据来自远端服务器，不能依赖 TypeScript 类型在运行时保证字段完整。 */
export function normalizeOpcUaPathSegments(value: unknown, fallbackName = ''): string[] {
  const normalized = Array.isArray(value)
    ? value.map(item => typeof item === 'string' ? item.trim() : '').filter(Boolean)
    : typeof value === 'string' && value.trim() ? [value.trim()] : []
  if (normalized.length) return normalized
  const fallback = typeof fallbackName === 'string' ? fallbackName.trim() : ''
  return fallback ? [fallback] : []
}

/** Element Plus 的 lazy load 会先传入 level=0、data=整棵数组的虚拟根节点。 */
export function isLazyBrowseRootNode(node: unknown): boolean {
  if (!node || typeof node !== 'object') return true
  const candidate = node as { level?: unknown; data?: unknown }
  return candidate.level === 0 || Array.isArray(candidate.data) || !candidate.data
}

export function toLazyBrowseTreeNode(node: OpcUaBrowsedNode): OpcUaLazyTreeNode {
  const name = typeof node?.name === 'string' && node.name.trim() ? node.name.trim() : String(node?.nodeId || '')
  const pathSegments = normalizeOpcUaPathSegments(node?.pathSegments, name)
  const nodeId = String(node?.nodeId || '')
  return {
    id: `node:${nodeId}|path:${JSON.stringify(pathSegments)}`,
    label: name,
    path: pathSegments.join('.'),
    sourceSegments: pathSegments,
    folder: !node?.isVariable,
    isLeaf: !node?.mayHaveChildren,
    nodeId,
    isVariable: !!node?.isVariable,
  }
}

export function flattenSelectedBranchTags(
  selections: Record<string, OpcUaBrowsedTag[]>,
): OpcUaBrowsedTag[] {
  const unique = new Map<string, OpcUaBrowsedTag>()
  for (const branch of Object.values(selections)) {
    for (const tag of branch) {
      const key = opcUaTagAliasKey(tag)
      if (!unique.has(key)) unique.set(key, tag)
    }
  }
  return [...unique.values()]
}

export function buildSelectedBranchRows(
  checkedKeys: string[],
  nodes: Record<string, OpcUaLazyTreeNode>,
  selections: Record<string, OpcUaBrowsedTag[]>,
  loadingKeys: string[],
  errors: Record<string, string>,
  truncated: Record<string, boolean> = {},
): SelectedBranchRow[] {
  const loading = new Set(loadingKeys)
  return checkedKeys.flatMap((id) => {
    const node = nodes[id]
    if (!node) return []
    const error = errors[id] || ''
    const status: SelectedBranchStatus = error
      ? 'error'
      : truncated[id]
        ? 'truncated'
        : loading.has(id)
          ? 'loading'
          : 'ready'
    return [{
      id,
      label: node.label,
      path: node.path || normalizeOpcUaPathSegments(node.sourceSegments, node.label).join('.'),
      status,
      tagCount: selections[id]?.length || 0,
      error,
    }]
  })
}
