<script setup lang="ts">
import { computed, ref } from 'vue'
import type { MesInterfaceListener } from '../api'
import { defaultInterfaceListener, mapJson, selectJsonPath } from '../utils/mesJsonPreview'
import MesJsonEditor from './MesJsonEditor.vue'
import MesJsonMappingEditor from './MesJsonMappingEditor.vue'
import JsonFieldInspector from './JsonFieldInspector.vue'
import { useI18n } from '../i18n'
const { t } = useI18n()
// editable=false：只读账号，原生输入框禁用(Element 输入框由外层 el-form 统一禁用)，样例预览照常可用
const props = withDefaults(defineProps<{ modelValue?: MesInterfaceListener; scopeGroup: string; editable?: boolean }>(), { editable: true })
const emit = defineEmits<{ 'update:modelValue': [value: MesInterfaceListener]; change: [] }>()
const config = computed(() => ({ ...defaultInterfaceListener(), ...props.modelValue }))
function set<K extends keyof MesInterfaceListener>(key: K, value: MesInterfaceListener[K]) { emit('update:modelValue', { ...config.value, [key]: value }); emit('change') }
const changes = computed({ get: () => config.value.changeFields.join('\n'), set: value => set('changeFields', value.split(/\r?\n/).map(v => v.trim()).filter(Boolean)) })
const headerDraft = ref<string | null>(null)
const headerError = ref('')
const headers = computed({ get: () => headerDraft.value ?? JSON.stringify(config.value.headers, null, 2), set: value => {
  headerDraft.value = value
  try { const parsed = JSON.parse(value); if (!parsed || Array.isArray(parsed) || typeof parsed !== 'object' || Object.values(parsed).some(v => typeof v !== 'string')) throw new Error(t('请求头必须是字符串值的 JSON 对象')); set('headers', parsed); headerError.value = '' }
  catch (error) { headerError.value = String(error) }
} })
const sample = ref('{"requestId":"DEMO-001","barcode":"VIN001","data":{"serialNo":"VIN001","count":2}}')
const source = computed(() => { try { return { value: JSON.parse(sample.value), error: '' } } catch (error) { return { value: null, error: String(error) } } })
const baseContext = computed(() => {
  try { if (source.value.error) throw new Error(source.value.error); const input = source.value.value; return { value: { input, mapped: mapJson(input, config.value.inputMappings), fields: null, vars: {} }, error: '' } }
  catch (error) { return { value: null, error: String(error) } }
})
const pathPreview = computed(() => { try { const input = JSON.parse(sample.value); const paths = [config.value.idField, ...config.value.changeFields].filter(Boolean); return { text: JSON.stringify(Object.fromEntries(paths.map(path => [path, selectJsonPath(input, path)])), null, 2), error: '' } } catch (error) { return { text: '', error: String(error) } } })
const sourceSample = computed(() => source.value.value)
</script>
<template><section class="listener-editor">
  <h4>{{ t('1 · 连接后台') }}</h4>
  <div class="hint">{{ t('网关主动连接后台；消息持久化后按顺序处理。任务未完成时下一件等待，失败保留重试。') }}</div>
  <el-form-item :label="t('监听协议')"><select :value="config.protocol" :disabled="!editable" aria-label="Listener protocol" @change="set('protocol', ($event.target as HTMLSelectElement).value as MesInterfaceListener['protocol'])"><option>SignalR</option><option>WebSocket</option><option>SSE</option></select></el-form-item>
  <el-form-item :label="t('后台连接地址')"><el-input :model-value="config.url" :placeholder="config.protocol === 'SignalR' ? 'https://example.com/barcodeHub' : config.protocol === 'SSE' ? 'https://example.com/events' : 'wss://example.com/events'" @update:model-value="set('url', $event)" /></el-form-item>
  <template v-if="config.protocol === 'SignalR'">
    <el-form-item :label="t('SignalR 接收方法名')"><el-input :model-value="config.receiveMethod" placeholder="ReceiveBarcode" @update:model-value="set('receiveMethod', $event)" /></el-form-item>
    <el-form-item :label="t('SignalR 订阅方法名（可选）')"><el-input :model-value="config.subscribeMethod" @update:model-value="set('subscribeMethod', $event)" /></el-form-item>
    <el-form-item :label="t('SignalR 回执方法名（可选）')"><el-input :model-value="config.replyMethod" @update:model-value="set('replyMethod', $event)" /></el-form-item>
    <div class="hint">{{ t('SignalR 使用 HTTP(S) Hub 地址。接收方法必须由后台发送一个 JSON 参数；订阅和回执模板是参数数组，方法名留空则不调用。') }}</div>
    <code>[{"station":"OP010"}]</code>
  </template>
  <label>{{ t('请求头 JSON') }}<textarea v-model="headers" :disabled="!editable" :aria-label="t('请求头 JSON')" rows="3" spellcheck="false" /></label><div v-if="headerError" role="alert">{{ headerError }}</div>
  <details><summary>{{ t('原始消息与路径预览（不连接后台）') }}</summary><div class="hint">{{ t('样例仅用于本地预览，不保存到生产配置；不会发送请求或下发设备命令。') }}</div><textarea v-model="sample" :aria-label="t('样例原始消息 JSON')" rows="5" /><JsonFieldInspector v-if="!source.error" data-inspector="input" :value="sourceSample" /><div v-if="pathPreview.error" role="alert">{{ t('JSON 预览错误') }}: {{ pathPreview.error }}</div><pre v-else>{{ pathPreview.text }}</pre></details>
  <div v-if="config.protocol === 'SSE'" class="hint">{{ t('SSE 仅使用 HTTP GET 接收事件，自动恢复 Last-Event-ID；不发送订阅报文或处理回执。') }}</div>
  <el-form-item v-if="config.protocol === 'SSE'" :label="t('SSE 事件名（空为全部）')"><el-input :model-value="config.eventName" @update:model-value="set('eventName', $event)" /></el-form-item>
  <details class="listener-section"><summary>{{ t('连接后发送订阅内容（后台要求时填写）') }}</summary>
  <p>{{ t('用途：连接建立后，向后台说明要监听哪个工位或主题。后台连接后就会主动推送时，可不配置订阅方法和内容。') }}</p>
  <MesJsonEditor :model-value="config.subscriptionTemplate" :context="{ taskId: 'demo-task', lastEventId: 'demo-event' }" :argument-array="config.protocol === 'SignalR'" :disabled="!editable || config.protocol === 'SSE'" :label="config.protocol === 'SignalR' ? t('SignalR 订阅参数 JSON 数组') : t('WebSocket 订阅 JSON')" @update:model-value="set('subscriptionTemplate', $event)" />
  <div v-if="config.protocol !== 'SSE'" class="hint">{{ t('订阅时仅支持 ${taskId} 和 ${lastEventId}，尚无 input 或 mapped。回执预览使用样例消息，fields 为 null。') }}</div>
  </details>
  <details class="listener-section"><summary>{{ t('处理完成后回复后台（可选）') }}</summary>
  <p>{{ t('回执表示本条监听消息的流程处理结果；它与流程中的结果上报是不同的接口内容。后台不需要确认时，可留空。') }}</p>
  <MesJsonEditor :model-value="config.responseTemplate" :argument-array="config.protocol === 'SignalR'" :context="baseContext.value" :context-error="baseContext.error" :disabled="!editable || config.protocol === 'SSE'" :label="config.protocol === 'SignalR' ? t('SignalR 回执参数 JSON 数组（流程提交后）') : t('WebSocket 处理回执 JSON（流程提交后）')" @update:model-value="set('responseTemplate', $event)" />
  <MesJsonEditor :model-value="config.ignoreResponseTemplate" :argument-array="config.protocol === 'SignalR'" :context="baseContext.value" :context-error="baseContext.error" :disabled="!editable || config.protocol === 'SSE'" :label="t('未变化或首次仅建基线回执模板（可选）')" @update:model-value="set('ignoreResponseTemplate', $event)" />
  <div v-if="config.protocol !== 'SSE'" class="hint">{{ t('未变化或仅建基线的消息使用独立回执模板。SignalR 模板仍为参数数组，留空不回执。') }}</div>
  </details>
  <h4>{{ t('2 · 识别新消息和变化') }}</h4>
  <p class="hint">{{ t('消息 ID 用来识别重复推送，例如 requestId。变化字段决定哪些值改变时触发流程，例如 barcode；留空则每条消息都处理。首次接入一般开启首条消息触发。') }}</p>
  <el-form-item :label="t('消息 ID 路径')"><el-input :model-value="config.idField" placeholder="requestId" @update:model-value="set('idField', $event)" /></el-form-item>
  <label>{{ t('变化字段路径（每行一个，空为每条消息）') }}<textarea v-model="changes" :disabled="!editable" :aria-label="t('变化字段路径')" rows="3" /></label>
  <el-checkbox :model-value="config.triggerOnFirst" @update:model-value="set('triggerOnFirst', Boolean($event))">{{ t('首条消息触发（关闭则仅建立基线）') }}</el-checkbox>
  <details><summary>{{ t('连接恢复和容量参数（高级）') }}</summary>
  <el-form-item :label="t('重连间隔 ms')"><el-input-number :model-value="config.reconnectMs" :min="100" :max="60000" @update:model-value="set('reconnectMs', $event ?? 1000)" /></el-form-item>
  <el-form-item :label="t('空闲超时秒')"><el-input-number :model-value="config.idleTimeoutSec" :min="1" :max="3600" @update:model-value="set('idleTimeoutSec', $event ?? 90)" /></el-form-item>
  <el-form-item :label="t('单条消息字节上限')"><el-input-number :model-value="config.maxMessageBytes" :min="64" :max="16777216" @update:model-value="set('maxMessageBytes', $event ?? 1048576)" /></el-form-item>
  </details>
  <h4>{{ t('3 · 读取后台消息字段') }}</h4>
  <div>{{ t('输入字段映射') }}</div><MesJsonMappingEditor :model-value="config.inputMappings" :editable="editable" :sample="sourceSample" :sample-error="source.error" @update:model-value="set('inputMappings', $event)" />
  <div class="hint">{{ t('ID 和变化字段路径均读取原始消息；模板 input 保留原始消息，mapped 为映射后的对象，例如 ${mapped.serialNo}。目标路径支持点分字段和数组索引。') }}</div>
</section></template>
<style scoped>
.listener-editor { width:100%; } textarea { display:block; width:100%; box-sizing:border-box; resize:vertical; font:12px/1.5 monospace; margin:5px 0; } .hint, details { font-size:12px; margin:8px 0; color:var(--el-text-color-secondary); } pre { white-space:pre-wrap; overflow-wrap:anywhere; } [role=alert] { color:var(--el-color-danger); } .el-form-item { margin:9px 0; }
.listener-editor h4{border-top:1px solid var(--el-border-color);padding-top:16px;margin:20px 0 12px;font-size:15px;color:var(--el-color-primary)}summary{cursor:pointer;font-weight:600}.listener-section{border:1px solid var(--el-border-color);border-radius:6px;padding:12px}.listener-section>p{line-height:1.8}
</style>
