export type ProjectShape = { channels: number; devices: number; groups: number; tags: number; mesTasks: number }

const SHAPE_FIELDS: Array<[keyof ProjectShape, string]> = [
  ['channels', '通道'],
  ['devices', '设备'],
  ['groups', '标记组'],
  ['tags', '点位'],
  ['mesTasks', 'MES 任务'],
]

export function emptyProjectShape(): ProjectShape {
  return { channels: 0, devices: 0, groups: 0, tags: 0, mesTasks: 0 }
}

export function projectShape(project: any): ProjectShape {
  const shape = emptyProjectShape()
  shape.mesTasks = (project?.mesUpload?.tasks || []).length
  shape.channels = (project?.channels || []).length
  const walk = (groups: any[]) => {
    for (const group of groups || []) {
      shape.groups++
      shape.tags += (group.tags || []).length
      walk(group.subGroups || [])
    }
  }
  for (const channel of project?.channels || []) for (const device of channel.devices || []) {
    shape.devices++
    shape.tags += (device.tags || []).length
    walk(device.groups || [])
  }
  return shape
}

export function shapeLoss(before: ProjectShape, after: ProjectShape): string[] {
  return SHAPE_FIELDS
    .filter(([key]) => after[key] < before[key])
    .map(([key, label]) => `${label} ${before[key]}→${after[key]}`)
}

/** 记录一次明确的用户删除所减少的结构数量，可累计多次删除。 */
export function authorizeDeletion(
  current: ProjectShape,
  before: ProjectShape,
  after: ProjectShape,
): ProjectShape {
  const result = { ...current }
  for (const [key] of SHAPE_FIELDS)
    result[key] += Math.max(0, before[key] - after[key])
  return result
}

/** 只返回超出用户明确删除额度的结构损失。 */
export function unapprovedShapeLoss(
  saved: ProjectShape,
  next: ProjectShape,
  authorized: ProjectShape,
): string[] {
  return SHAPE_FIELDS
    .filter(([key]) => Math.max(0, saved[key] - next[key]) > authorized[key])
    .map(([key, label]) => `${label} ${saved[key]}→${next[key]}`)
}

/** 保存成功后消费快照中已有的授权；保存过程中新增的删除授权继续留给下一次保存。 */
export function consumeDeletionAuthorization(current: ProjectShape, consumed: ProjectShape): ProjectShape {
  const result = emptyProjectShape()
  for (const [key] of SHAPE_FIELDS)
    result[key] = Math.max(0, current[key] - consumed[key])
  return result
}
