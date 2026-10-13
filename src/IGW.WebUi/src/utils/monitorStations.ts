export function toggleStationSelection(current: ReadonlySet<string>, name: string): Set<string> {
  const next = new Set(current)
  if (next.has(name)) next.delete(name)
  else next.add(name)
  return next
}

export function filterEntriesByStations(entries: any[], selected: ReadonlySet<string>): any[] {
  if (!selected.size) return entries
  return entries.filter((entry: any) =>
    selected.has(String(entry?.channel || ''))
    || (entry?.impacted || []).some((child: any) => selected.has(String(child?.channel || ''))),
  )
}

export function retainExistingStations(
  current: ReadonlySet<string>,
  channels: Array<{ name: string }> | null | undefined,
): Set<string> {
  const existing = new Set((channels || []).map((channel) => channel.name))
  return new Set([...current].filter((name) => existing.has(name)))
}
