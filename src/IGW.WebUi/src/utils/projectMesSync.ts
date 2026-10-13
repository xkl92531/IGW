/**
 * 将采集流程窗口保存后的 MES 配置同步回工程页。
 *
 * 工程树使用对象身份生成稳定节点 key，因此这里不能用服务端快照替换整个工程；
 * 否则通道、设备和组都会获得新身份，已展开和已选中的树节点随之失效。
 */
export function applyMesFlowSnapshot(localProject: any, remoteProject: any): any {
  if (!localProject || !remoteProject) return remoteProject
  localProject.mesUpload = structuredClone(remoteProject.mesUpload ?? { tasks: [] })
  return localProject
}

const normalizedGroupRef = (task: any) => String(task?.groupRef || '').trim().toLowerCase()
const isAncestorRef = (ancestor: string, descendant: string) =>
  ancestor.length > 0 && descendant.startsWith(`${ancestor}.`)

export type MesTaskLocator = string | { groupRef?: string; id?: string }

/**
 * 单任务操作以完整 groupRef 为主键。旧任务 ID 仅在唯一时才允许回退匹配，
 * 避免复制/导入工程中的重复 ID 把另一个数据块误停用。
 */
function resolveMesTask(mesUpload: any, locator: MesTaskLocator): any | null {
  const tasks = Array.isArray(mesUpload?.tasks) ? mesUpload.tasks : []
  const requestedGroup = typeof locator === 'object'
    ? String(locator?.groupRef || '').trim().toLowerCase()
    : String(locator || '').trim().toLowerCase()
  if (requestedGroup) {
    const byGroup = tasks.filter((task: any) => normalizedGroupRef(task) === requestedGroup)
    if (byGroup.length === 1) return byGroup[0]
    if (typeof locator === 'object' && locator?.groupRef) return null
  }

  const requestedId = typeof locator === 'object'
    ? String(locator?.id || '').trim()
    : String(locator || '').trim()
  if (!requestedId) return null
  const byId = tasks.filter((task: any) => String(task?.id || '') === requestedId)
  return byId.length === 1 ? byId[0] : null
}

/** 旧工程没有 flowAvailable 字段；已有任务按已开放处理，保证升级后仍能进入原流程。 */
export function isMesFlowAvailable(task: any): boolean {
  return !!task && task.flowAvailable !== false
}

function preferredTasks(tasks: any[]): any[] {
  return tasks.filter((task) => {
    const current = normalizedGroupRef(task)
    if (!current) return true
    return !tasks.some((candidate) => candidate !== task && isAncestorRef(current, normalizedGroupRef(candidate)))
  })
}

/** 查找会与目标任务形成祖先/后代重复采集的已启用任务；同级任务不冲突。 */
export function findMesTaskEnableConflict(mesUpload: any, taskLocator: MesTaskLocator): string {
  const target = resolveMesTask(mesUpload, taskLocator)
  if (!target) return ''
  return findMesGroupEnableConflict(mesUpload, target.groupRef, target)
}

/** 未创建任务的组也能预先检查父子互斥，供属性开关直接使用。 */
export function findMesGroupEnableConflict(mesUpload: any, groupRef: string, excludedTask?: any): string {
  const tasks = Array.isArray(mesUpload?.tasks) ? mesUpload.tasks : []
  const targetRef = String(groupRef || '').trim().toLowerCase()
  if (!targetRef) return ''
  const conflict = tasks.find((task: any) => {
    if (task === excludedTask || !isMesFlowAvailable(task) || task?.enabled !== true) return false
    const otherRef = normalizedGroupRef(task)
    return isAncestorRef(targetRef, otherRef) || isAncestorRef(otherRef, targetRef)
  })
  return String(conflict?.groupRef || '')
}

/** 流程功能按父子边界互斥；运行状态不参与判断，避免“停运”被误当成“关闭功能”。 */
export function findMesGroupAvailabilityConflict(mesUpload: any, groupRef: string, excludedTask?: any): string {
  const tasks = Array.isArray(mesUpload?.tasks) ? mesUpload.tasks : []
  const targetRef = String(groupRef || '').trim().toLowerCase()
  if (!targetRef) return ''
  const conflict = tasks.find((task: any) => {
    if (task === excludedTask || !isMesFlowAvailable(task)) return false
    const otherRef = normalizedGroupRef(task)
    return isAncestorRef(targetRef, otherRef) || isAncestorRef(otherRef, targetRef)
  })
  return String(conflict?.groupRef || '')
}

/** 为属性开关首次启用的组创建与流程设计器一致的空任务。 */
export function ensureMesTaskForGroup(
  mesUpload: any,
  groupRef: string,
  taskId: string,
): { task: any; created: boolean } {
  const tasks = Array.isArray(mesUpload?.tasks) ? mesUpload.tasks : (mesUpload.tasks = [])
  const normalizedRef = String(groupRef || '').trim()
  const existing = tasks.find((task: any) => normalizedGroupRef(task) === normalizedRef.toLowerCase())
  if (existing) return { task: existing, created: false }

  const segments = normalizedRef.split('.').filter(Boolean)
  const baseName = segments[segments.length - 1] || '采集任务'
  let name = baseName
  for (let n = 2; tasks.some((task: any) => task?.name === name); n++) name = `${baseName}-${n}`
  const task = {
    id: taskId,
    name,
    groupRef: normalizedRef,
    deviceRef: segments.slice(0, 2).join('.'),
    flowAvailable: true,
    enabled: false,
    collectIntervalMs: 1000,
    confirmRewrite: true,
    payload: { mode: 'DataRanges', dataRanges: [], fields: [], structs: [], append: {} },
    steps: [],
    flow: { nodes: [], edges: [] },
  }
  tasks.push(task)
  return { task, created: true }
}

/**
 * 设备批量配置只开放直属第一层标记组。更深层任务停止运行并关闭功能，
 * 但完整保留节点、映射和回写配置，之后仍可由用户重新选择层级。
 */
export function configureDeviceFirstLevelMesFlows(
  mesUpload: any,
  channelName: string,
  deviceName: string,
  directGroups: any[],
  createId: () => string,
): { direct: number; created: number; opened: number; closed: number } {
  const tasks = Array.isArray(mesUpload?.tasks) ? mesUpload.tasks : (mesUpload.tasks = [])
  const deviceRef = `${channelName}.${deviceName}`
  const directRefs = new Set((directGroups || []).map((group) => `${deviceRef}.${group.name}`.toLowerCase()))
  let created = 0
  let opened = 0
  let closed = 0

  for (const task of tasks) {
    const current = normalizedGroupRef(task)
    if (!current.startsWith(`${deviceRef.toLowerCase()}.`) || directRefs.has(current)) continue
    if (isMesFlowAvailable(task) || task.enabled === true) closed++
    task.flowAvailable = false
    task.enabled = false
  }

  for (const group of directGroups || []) {
    const groupRef = `${deviceRef}.${group.name}`
    const ensured = ensureMesTaskForGroup(mesUpload, groupRef, createId())
    if (ensured.created) created++
    if (ensured.created || !isMesFlowAvailable(ensured.task)) opened++
    ensured.task.flowAvailable = true
  }

  return { direct: directRefs.size, created, opened, closed }
}

/**
 * 属性中的“采集流程”开关只控制功能是否可用：
 * 开启不启动任务；关闭会强制停止任务，但完整保留流程配置。
 */
export function setMesTaskFlowAvailable(
  mesUpload: any,
  taskLocator: MesTaskLocator,
  available: boolean,
): { changed: boolean; conflictGroupRef: string } {
  const target = resolveMesTask(mesUpload, taskLocator)
  if (!target) return { changed: false, conflictGroupRef: '' }
  if (!available) {
    const changed = isMesFlowAvailable(target) || target.enabled === true
    target.flowAvailable = false
    target.enabled = false
    return { changed, conflictGroupRef: '' }
  }
  const conflictGroupRef = findMesGroupAvailabilityConflict(mesUpload, target.groupRef, target)
  if (conflictGroupRef) return { changed: false, conflictGroupRef }
  const changed = !isMesFlowAvailable(target)
  target.flowAvailable = true
  return { changed, conflictGroupRef: '' }
}

/** 单任务开关：关闭始终允许；开启时祖先与后代互斥，同级可以同时运行。 */
export function setMesTaskEnabled(
  mesUpload: any,
  taskLocator: MesTaskLocator,
  enabled: boolean,
): { changed: boolean; conflictGroupRef: string } {
  const target = resolveMesTask(mesUpload, taskLocator)
  if (!target) return { changed: false, conflictGroupRef: '' }
  if (!enabled) {
    const changed = target.enabled === true
    target.enabled = false
    return { changed, conflictGroupRef: '' }
  }
  if (!isMesFlowAvailable(target)) return { changed: false, conflictGroupRef: '' }
  const conflictGroupRef = findMesTaskEnableConflict(mesUpload, taskLocator)
  if (conflictGroupRef) return { changed: false, conflictGroupRef }
  const changed = target.enabled !== true
  target.enabled = true
  return { changed, conflictGroupRef: '' }
}

/** 只有从运行中切换到停用时请求确认；确认弹窗取消通常以 Promise reject 表达。 */
export async function confirmMesTaskDisable(
  currentEnabled: boolean,
  nextEnabled: boolean,
  requestConfirmation: () => Promise<unknown>,
): Promise<boolean> {
  if (!currentEnabled || nextEnabled) return true
  try {
    await requestConfirmation()
    return true
  } catch {
    return false
  }
}

/** 带停用确认的单任务状态切换；取消确认时不得改变原任务状态。 */
export async function setMesTaskEnabledWithConfirmation(
  mesUpload: any,
  taskLocator: MesTaskLocator,
  enabled: boolean,
  requestConfirmation: () => Promise<unknown>,
): Promise<{ changed: boolean; conflictGroupRef: string; cancelled: boolean }> {
  const target = resolveMesTask(mesUpload, taskLocator)
  if (!target) return { changed: false, conflictGroupRef: '', cancelled: false }
  const confirmed = await confirmMesTaskDisable(target.enabled === true, enabled, requestConfirmation)
  if (!confirmed) return { changed: false, conflictGroupRef: '', cancelled: true }
  return { ...setMesTaskEnabled(mesUpload, taskLocator, enabled), cancelled: false }
}

/** 工程配置的设备/通道批量操作；流程画布不再暴露“全部任务”开关。 */
export function setAllMesTasksEnabled(mesUpload: any, enabled: boolean, scopeGroupRef = ''): number {
  if (!mesUpload) return 0
  const tasks = Array.isArray(mesUpload.tasks) ? mesUpload.tasks : []
  const scope = String(scopeGroupRef || '').trim().toLowerCase()
  const scopedTasks = scope
    ? tasks.filter((task: any) => {
      const current = normalizedGroupRef(task)
      return current === scope || current.startsWith(`${scope}.`)
    })
    : tasks
  const availableTasks = scopedTasks.filter(isMesFlowAvailable)
  const targets = enabled ? new Set(preferredTasks(availableTasks)) : new Set(scopedTasks)
  for (const task of scopedTasks) task.enabled = enabled && targets.has(task)
  // 旧字段继续写回以便导出文件可读，但后台不再用它额外拦截任务。
  mesUpload.enabled = tasks.some((task: any) => task?.enabled === true)
  return enabled ? targets.size : scopedTasks.length
}
