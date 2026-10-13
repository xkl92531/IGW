/** Application envelope shared by live-state consumers, independent of the transport. */
export interface RealtimeFrame {
  event: string
  id: string
  data: string
}
