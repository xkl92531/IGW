import type { ArrayDef } from '../../utils/arrayTags'

export type ProjectNodeKind = 'project' | 'channel' | 'device' | 'group' | 'tag'
export type ProjectMutationKind = 'upsertEntity' | 'deleteEntities' | 'moveEntities' | 'copyEntities' | 'setEnabled' | 'replaceMesTask' | 'replaceProjectSections' | 'saveMesTasks' | 'batch'
export type ProjectMesTaskScope = 'all' | 'device' | 'channel'

export interface ProjectTreeNodeDto {
  id: string
  kind: ProjectNodeKind
  name: string
  description: string | null
  actualPath: string
  hasChildren: boolean
  directTagCount: number
  rowVersion: number
  schemaVersion: number
}

export interface ProjectSummaryDto {
  root: ProjectTreeNodeDto
  totalTagCount: number
  fullProjectSupported: boolean
  configurationDegraded: boolean
  configurationDegradedMessage: string | null
}

export interface ProjectTagRowDto {
  id: string
  name: string
  actualPath: string
  address: string
  dataType: string
  access: string
  scanRateMs: number
  description: string | null
  engUnit: string | null
  stringLength: number | null
  array: ArrayDef | null
  metadata: Record<string, string> | null
  rowVersion: number
}

export interface ProjectPageDto<T> {
  items: T[]
  total: number
  offset: number
  limit: number
}

export interface ProjectTagQuery {
  groupId?: string
  deviceId?: string
  q?: string
  offset: number
  limit: number
}

export interface ProjectMesTaskQuery {
  scope: ProjectMesTaskScope
  deviceId?: string
  channelId?: string
  q?: string
  offset: number
  limit: number
  enabled?: boolean
}

export interface ProjectMesTaskRowDto {
  id: string
  name: string
  groupId: string | null
  groupRef: string | null
  deviceRef: string | null
  deviceId?: string | null
  channelId?: string | null
  description?: string | null
  enabled: boolean
  flowAvailable: boolean
  rowVersion: number
  collectIntervalMs?: number
}

export interface ProjectMutationRequest {
  kind: ProjectMutationKind
  entityKind: ProjectNodeKind
  entityIds: readonly string[]
  targetId: string | null
  documentJson: string | null
}

export interface ProjectMutationResponse {
  applied: boolean
  revision: string
  /** 局部修改删掉了 MES 任务绑定的组：服务端已清理失效任务，客户端应重新取 MES 配置。 */
  mesTasksReconciled?: boolean
}

export type ProjectEntityDocumentDto = Record<string, unknown>
export type ProjectMesTaskDocumentDto = Record<string, unknown> & { id: string; name: string }
export interface ProjectSettingsDocumentDto {
  settings: Record<string, unknown> & { restPort?: number; opcUaPort?: number }
  opcUa: Record<string, unknown> & {
    enabled?: boolean
    port?: number
    allowAnonymous?: boolean
    allowEncryption?: boolean
  }
}

export type KepIssueSeverity = 'info' | 'warning' | 'error'

export interface KepCompatibilityIssueDto {
  path: string
  code: string
  severity: KepIssueSeverity
  message: string
}

export interface KepCompatibilityReportDto {
  issues: KepCompatibilityIssueDto[]
  canImportIntoKep?: boolean
}

export interface ProjectImportPreviewDto {
  token: string
  fileHash: string
  channels: number
  devices: number
  groups: number
  tags: number
  mesTasks: number
  hasPortableSecrets: boolean
  compatibility: KepCompatibilityReportDto
}

export interface ProjectImportResultDto {
  applied: boolean
  revision: string
  errorCode: string | null
  compatibility: KepCompatibilityReportDto
}

export interface ProjectExportOptions {
  includePortableSecrets: boolean
}

/** 大工程按需加载：设备/组尚未载入时，只知道它的下级数量。 */
export interface LazyCountsDto {
  groups: number
  tags: number
  totalTags: number
}

export interface ProjectPathMatchDto {
  path: string
  found: boolean
  kind: 'tag' | 'group' | 'device' | 'channel' | null
  id: string | null
  parentId: string | null
  parentPath: string | null
  parentKind: 'group' | 'device' | 'channel' | 'project' | null
}

export interface ProjectShellDto {
  project: any
  lazy: Record<string, LazyCountsDto>
}

export interface ProjectLazyChildrenDto {
  groups: any[]
  tags: any[]
  lazy: Record<string, LazyCountsDto>
}
