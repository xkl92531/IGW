export type PresentationTranslate = (
  source: string,
  params?: Record<string, string | number>,
) => string

export type ProjectTreeSystemKind = 'project' | 'connectivity' | 'channel' | 'device' | 'group'

const treeLabels: Record<ProjectTreeSystemKind, string> = {
  project: '项目',
  connectivity: '连接性',
  channel: '通道',
  device: '设备',
  group: '组',
}

export function systemTreeLabel(kind: ProjectTreeSystemKind, translate: PresentationTranslate): string {
  return translate(treeLabels[kind])
}

/** 标记组说明属于工程业务数据；系统节点与设备节点不在树中重复显示说明。 */
export function projectTreeDescription(
  kind: ProjectTreeSystemKind | string,
  target: { description?: unknown } | null | undefined,
): string {
  return kind === 'group' ? String(target?.description ?? '').trim() : ''
}

export function collectionStateMark(
  kind: 'group' | 'channel' | 'device',
  enabled: boolean | undefined,
  activeTaskCount: number,
  translate: PresentationTranslate,
): { text: string; color: string } | null {
  if (kind === 'group') {
    if (enabled == null) return null
    return enabled
      ? { text: translate('● 采集中'), color: '#1B7F37' }
      : { text: translate('○ 采集停用'), color: '#999' }
  }
  return activeTaskCount > 0
    ? { text: translate('● {count} 组采集', { count: activeTaskCount }), color: '#1B7F37' }
    : null
}

export function mesFlowDialogTitle(groupRef: string, translate: PresentationTranslate): string {
  const groupName = groupRef.split('.').pop() || translate('数据块')
  return `${translate('MES 采集流程')} — ${groupName}`
}

/** 采集流程功能开关独立于运行开关；旧任务缺少字段时保持可配置。 */
export function isMesFlowConfigurationDisabled(
  task: { flowAvailable?: boolean } | null | undefined,
): boolean {
  return !task || task.flowAvailable === false
}
