export function cloneReactiveValue<T>(value: T): T {
  // 配置最终会以 JSON 发送到 Runtime。直接按同一序列化规则复制，既能
  // 递归解除 Vue Proxy，也不会把嵌套的响应式对象交给 structuredClone。
  // structuredClone(toRaw(root)) 只解包根对象；节点/分支等子对象仍可能
  // 是 Proxy，浏览器会抛 DataCloneError 并让“保存并应用”在请求前中断。
  const json = JSON.stringify(value)
  return (json === undefined ? value : JSON.parse(json)) as T
}
