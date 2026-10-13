<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { LogCenterEntryDto, LogPayloadExchangeDto } from '../../api'
import { logAttributeRows } from '../../utils/logAttributes'
import { buildLogPresentation, logFeatureLabel, logModuleLabel } from '../../utils/logPresentation'
import { parseLogTagValues, tagPathMatches, TagValuesField } from '../../utils/logTagValues'
import { useI18n } from '../../i18n'
import ExpandableLogValue from './ExpandableLogValue.vue'

/** highlightPath：按工程路径(右键「查看日志」)定位到这条记录时，点位值表里标出这个点(或这个组下的点)。 */
const props = defineProps<{ entry: LogCenterEntryDto | null; payload?: LogPayloadExchangeDto | null; payloadLoading?: boolean; highlightPath?: string | null }>()
const emit = defineEmits<{ close: []; 'load-payload': []; 'download-payload': [] }>()
/** JSON 报文自动缩进排版，便于阅读；其他内容原样显示。 */
function pretty(text: string) {
  const trimmed = (text || '').trim()
  if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) return text
  try { return JSON.stringify(JSON.parse(trimmed), null, 2) } catch { return text }
}
const { locale, t } = useI18n()
// 采集/质量变化记录带的点位值单独成表，解析成功就不在结构化字段里重复一大段 JSON
const tagValues = computed(() => parseLogTagValues(props.entry?.attributes?.[TagValuesField]))
const attributes = computed(() => logAttributeRows(props.entry?.attributes)
  .filter(row => !(tagValues.value && row.key === TagValuesField)))
const valuesScroll = ref<HTMLElement>()
// 一块几百个点时，被定位的点可能在列表很靠下：自动滚到第一行标出的点
watch([tagValues, () => props.highlightPath], async () => {
  await nextTick()
  const box = valuesScroll.value
  const row = box?.querySelector<HTMLElement>('tr.highlighted')
  if (box && row) box.scrollTop = Math.max(0, row.offsetTop - box.clientHeight / 3)
}, { immediate: true })
const presentation = computed(() => props.entry ? buildLogPresentation(props.entry) : null)
function time(value: string) { return new Date(value).toLocaleString(locale.value === 'en-US' ? 'en-US' : 'zh-CN', { hour12: false }) }
</script>

<template>
  <el-drawer :model-value="!!entry" size="min(760px, 94vw)" append-to-body :title="t('事件详情')" @close="emit('close')">
    <article v-if="entry" class="detail-sheet">
      <header><span :class="['level', entry.level.toLowerCase()]">{{ entry.level }}</span><time>{{ time(entry.timestamp) }}</time></header>
      <div class="event-message"><ExpandableLogValue :value="entry.message" :threshold="220" /></div>
      <div class="identity">
        <span>{{ t('模块') }}</span><b>{{ t(logModuleLabel(presentation?.module || '')) }}</b>
        <span>{{ t('功能') }}</span><b>{{ t(logFeatureLabel(presentation?.feature || '')) }}</b>
        <span>{{ t('任务') }}</span><b>{{ presentation?.task || '—' }}</b>
        <span>{{ t('接口调用') }}</span><code><ExpandableLogValue :value="presentation?.interfaceOperation || '—'" /></code>
        <span>{{ t('采集范围') }}</span><code><ExpandableLogValue :value="presentation?.plcScope || '—'" /></code>
        <span>{{ t('日志来源') }}</span><code><ExpandableLogValue :value="String(entry.attributes?.igw_logger || '—')" /></code>
        <span>{{ t('排障编号') }}<small>{{ t('技术支持使用') }}</small></span><code><ExpandableLogValue :value="entry.traceId || '—'" :always-copy="!!entry.traceId" /></code>
      </div>
      <section v-if="entry.category === 'interface' || entry.payloadId" data-testid="actual-http-exchange" class="payload-section"><h4>{{ t('实际接口报文') }}</h4>
        <template v-if="entry.payloadId">
          <div class="payload-actions"><el-button :loading="payloadLoading" @click="emit('load-payload')">{{ t(payload ? '重新读取' : '读取报文') }}</el-button><el-button @click="emit('download-payload')">{{ t('下载完整报文') }}</el-button></div>
          <template v-if="payload">
            <div class="exchange-block" data-testid="exchange-request">
              <div class="exchange-title">{{ t(payload.split ? '发送内容' : '报文内容') }}<small v-if="payload.requestTruncated">{{ t('内容较长，只显示开头部分，完整内容请下载') }}</small></div>
              <ExpandableLogValue class="payload-shell" :value="pretty(payload.request) || t('（无内容）')" :threshold="600" always-copy start-expanded />
            </div>
            <div v-if="payload.split" class="exchange-block" data-testid="exchange-response">
              <div class="exchange-title">{{ t('返回内容') }}<small v-if="payload.responseTruncated">{{ t('内容较长，只显示开头部分，完整内容请下载') }}</small></div>
              <ExpandableLogValue class="payload-shell" :value="pretty(payload.response) || t('（无内容）')" :threshold="600" always-copy start-expanded />
            </div>
          </template>
        </template>
        <p v-else class="payload-empty">{{ t('这条接口日志没有关联请求与响应正文；升级前的历史记录无法补录。') }}</p>
      </section>
      <section v-if="tagValues" data-testid="log-tag-values" class="tag-values-section"><h4>{{ t('点位值') }}<small>{{ t('本批次读到的值') }}</small></h4>
        <p v-if="tagValues.badOnly" class="tag-values-note">{{ t('只列出质量异常的点位') }}</p>
        <p v-if="tagValues.omitted > 0" data-testid="log-tag-values-omitted" class="tag-values-note">{{ t('共 {total} 个点位，只记录了 {shown} 个（异常点位优先），其余 {omitted} 个未记录', { total: tagValues.total, shown: tagValues.rows.length, omitted: tagValues.omitted }) }}</p>
        <div ref="valuesScroll" class="tag-values-scroll"><table>
          <thead><tr><th>{{ t('点位') }}</th><th>{{ t('值') }}</th><th>{{ t('质量') }}</th></tr></thead>
          <tbody><tr v-for="(row, index) in tagValues.rows" :key="`${index}:${row.path}`" data-testid="log-tag-value-row" :class="{ bad: !row.good, highlighted: tagPathMatches(row.path, highlightPath) }">
            <td class="tag-path">{{ row.path }}</td><td class="tag-value">{{ row.value }}</td><td class="tag-quality">{{ row.quality || t('正常') }}</td>
          </tr></tbody>
        </table></div>
      </section>
      <section data-testid="structured-log-fields"><h4>{{ t('结构化字段') }}</h4><dl><template v-for="row in attributes" :key="row.key"><dt data-testid="log-attribute-key" class="attribute-key" :title="row.key">{{ t(row.label) }}</dt><dd><ExpandableLogValue :value="row.value" /></dd></template></dl></section>
    </article>
  </el-drawer>
</template>

<style scoped>
.detail-sheet { color:#344256; }.detail-sheet>header { display:flex; align-items:center; justify-content:space-between; gap:12px; }.detail-sheet time { color:#7c8999; font:11px Consolas,monospace; }.level { padding:3px 7px; border-radius:5px; background:#edf2f7; color:#5f6d7d; font-size:10px; font-weight:700; text-transform:uppercase; }.level.warn,.level.warning { background:#fff3d5; color:#946400; }.level.error,.level.critical { background:#fde6e3; color:#a0372d; }
.event-message { margin:16px 0; color:#25364a; font-size:17px; font-weight:600; line-height:1.55; }.identity { display:grid; grid-template-columns:92px minmax(0,1fr); gap:8px 12px; padding:12px; border-radius:8px; background:#f5f8fb; font-size:12px; }.identity span { color:#7d8999; }.identity span small { display:block; margin-top:2px; font-size:10px; color:#a0a9b5; }.identity code { min-width:0; overflow-wrap:anywhere; }
section { margin-top:20px; }h4 { margin:0 0 9px; color:#506178; font-size:12px; letter-spacing:.04em; }dl { display:grid; grid-template-columns:145px minmax(0,1fr); margin:0; border-top:1px solid #e8edf2; }dt,dd { min-width:0; margin:0; padding:8px; border-bottom:1px solid #e8edf2; font-size:12px; }dt { color:#718094; }.attribute-key { overflow-wrap:anywhere; word-break:break-word; }dd { overflow-wrap:anywhere; color:#2f4055; font-family:Consolas,monospace; }.payload-note { margin:0 0 9px; color:#7b8796; font-size:11px; line-height:1.55; }.payload-empty { margin:0; padding:12px; border:1px dashed #d8e1ec; border-radius:7px; background:#f8fafc; color:#718096; font-size:12px; line-height:1.6; }.payload-actions { display:flex; flex-wrap:wrap; gap:8px; margin-bottom:9px; }.payload-page-meta { display:flex; flex-wrap:wrap; justify-content:space-between; gap:6px 12px; margin:0 0 6px; color:#718096; font-size:11px; }.exchange-block { margin-bottom:12px; }.exchange-title { display:flex; align-items:baseline; gap:8px; margin:0 0 6px; color:#2f4055; font-size:12px; font-weight:700; }.exchange-title small { color:#9a7a39; font-weight:400; }.payload-shell { padding:10px; border:1px solid #e0e7ef; border-radius:7px; background:#f7f9fc; font:11px/1.6 Consolas,monospace; }
.tag-values-section h4 small { margin-left:8px; color:#9aa5b3; font-weight:400; letter-spacing:0; }.tag-values-note { margin:0 0 7px; color:#8a6a2c; font-size:11px; line-height:1.5; }
.tag-values-scroll { max-height:min(46vh,420px); overflow:auto; border:1px solid #e3e9f0; border-radius:7px; }.tag-values-scroll table { width:100%; border-collapse:collapse; table-layout:fixed; font-size:12px; }
.tag-values-scroll th { position:sticky; top:0; z-index:1; padding:6px 8px; background:#f3f6fa; color:#6c7a8c; font-weight:600; text-align:left; }.tag-values-scroll th:nth-child(1) { width:46%; }.tag-values-scroll th:nth-child(3) { width:22%; }
.tag-values-scroll td { padding:5px 8px; border-top:1px solid #edf1f5; color:#2f4055; font-family:Consolas,monospace; overflow-wrap:anywhere; vertical-align:top; }.tag-values-scroll .tag-quality { color:#2f8a50; }
.tag-values-scroll tr.bad .tag-quality { color:#a0372d; font-weight:600; }.tag-values-scroll tr.highlighted td { background:#fff6d9; }.tag-values-scroll tr.highlighted .tag-path { font-weight:700; }
@media (max-width:560px) { .identity { grid-template-columns:1fr; }.identity span { margin-top:5px; }dl { grid-template-columns:1fr; }dt { padding-bottom:3px; border-bottom:0; }dd { padding-top:3px; } }
</style>
