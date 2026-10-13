<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from '../i18n'
const model = defineModel<any>({ required: true })
// editable=false：只看协议参数，输入框全部禁用
const props = withDefaults(defineProps<{ driverId: string; editable?: boolean }>(), { editable: true })
const { t } = useI18n()
const fields = [
  { key: 'unitId', label: '从站号 Unit ID', min: 0, max: 255, value: '1' },
  { key: 'timeoutMs', label: '请求超时（毫秒）', min: 1, max: 2147483647, value: '2000' },
  { key: 'regBlock', label: '每次读取寄存器上限', min: 1, max: 125, value: '120' },
  { key: 'coilBlock', label: '每次读取线圈上限', min: 1, max: 2000, value: '512' },
]
function set(key: string, event: Event) { if (!props.editable) return; model.value = { ...model.value, [key]: (event.target as HTMLInputElement).value } }
const base = computed(() => ['true', '1', '启用', 'enabled', 'yes'].includes(String(model.value.zeroBased).toLowerCase()) ? 'true' : 'false')
</script>
<template>
  <section class="protocol-settings" :aria-label="`${driverId} settings`">
    <p class="title">Modbus TCP</p>
    <p class="note">{{ t('连接端点示例：192.168.0.1:502') }}</p>
    <label v-for="field in fields" :key="field.key">{{ t(field.label) }}<input :aria-label="field.key" type="number" :min="field.min" :max="field.max" :disabled="!editable" :value="model[field.key] ?? field.value" @change="set(field.key, $event)" /></label>
    <label>{{ t('旧数字地址起点') }}<select aria-label="zeroBased" :disabled="!editable" :value="base" @change="set('zeroBased', $event)"><option value="false">{{ t('1 基：40001 = 首寄存器') }}</option><option value="true">{{ t('0 基：40000 = 首寄存器') }}</option></select></label>
    <label>{{ t('字节 / 字顺序（32/64 位）') }}<select aria-label="wordOrder" :disabled="!editable" :value="model.wordOrder || 'ABCD'" @change="set('wordOrder', $event)"><option value="ABCD">ABCD</option><option value="CDAB">CDAB</option><option value="BADC">BADC</option><option value="DCBA">DCBA</option></select></label>
    <label>{{ t('写入功能码策略') }}<select aria-label="writeMode" :disabled="!editable" :value="model.writeMode || 'auto'" @change="set('writeMode', $event)"><option value="auto">{{ t('自动：单值 FC05/06，连续值 FC15/16') }}</option><option value="single">{{ t('单写：FC05 / FC06') }}</option><option value="multiple">{{ t('多写：FC15 / FC16') }}</option></select></label>
    <p class="note">{{ t('HR:0 等显式地址始终从零开始，不受旧数字地址起点影响。寄存器位写使用读-改-写；单写策略将连续值拆成多个请求，不保证整组原子写入。') }}</p>
  </section>
</template>
<style scoped>
.protocol-settings{box-sizing:border-box;width:100%;font-size:13px;line-height:1.6}.title{font-size:15px;font-weight:600;color:var(--el-color-primary);margin-top:0}label{display:grid;grid-template-columns:185px minmax(0,1fr);align-items:center;gap:10px;margin-bottom:12px}input,select{width:100%;box-sizing:border-box;min-width:0;padding:6px 9px;border:1px solid var(--el-border-color);border-radius:4px;color:var(--el-text-color-primary);background:var(--el-bg-color);font:inherit}.note{color:var(--el-text-color-secondary)}
</style>
