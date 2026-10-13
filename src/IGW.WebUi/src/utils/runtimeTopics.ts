/** 页面按职责订阅运行主题，避免全局连接接收 MES 高频任务状态。 */
export function globalRuntimeTopics(): string[] {
  return ['devices', 'mes-availability']
}

/** 流程设计页只接收当前任务的权威状态与有序流转事件。 */
export function mesFlowTopics(taskId: string): string[] {
  return [`mes-state:${taskId}`, `mes-task:${taskId}`]
}

/** MES 状态页打开时才订阅所有任务状态。 */
export function mesStatusTopics(): string[] {
  return ['mes-state']
}
