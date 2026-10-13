export interface QuickClientScopeNode {
  path: string
  children: QuickClientScopeNode[]
}

export async function loadFirstPopulatedScope<TNode extends QuickClientScopeNode>(
  root: TNode,
  load: (node: TNode) => Promise<number>,
  maxVisitedScopes = 256,
): Promise<TNode | null> {
  const pending: TNode[] = [root]
  const visited = new Set<string>()
  while (pending.length && visited.size < maxVisitedScopes) {
    const node = pending.pop()!
    if (visited.has(node.path)) continue
    visited.add(node.path)
    const itemCount = await load(node)
    if (itemCount < 0) return null
    if (itemCount > 0) return node
    for (let index = node.children.length - 1; index >= 0; index--)
      pending.push(node.children[index] as TNode)
  }
  return null
}

/** Reload a known selection through its exact ancestor chain without switching to another PLC. */
export async function loadExactScope<TNode extends QuickClientScopeNode>(
  root: TNode,
  load: (node: TNode) => Promise<number>,
  path: string,
  maxVisitedScopes = 256,
): Promise<TNode | null> {
  if (root.path && path !== root.path && !path.startsWith(`${root.path}.`)) return null
  let node: TNode | undefined = root
  const visited = new Set<string>()
  while (node && visited.size < maxVisitedScopes) {
    if (visited.has(node.path)) return null
    visited.add(node.path)
    if (await load(node) < 0) return null
    if (node.path === path) return node
    node = node.children.filter(child => child.path === path || path.startsWith(`${child.path}.`))
      .sort((a, b) => b.path.length - a.path.length)[0] as TNode | undefined
  }
  return null
}
