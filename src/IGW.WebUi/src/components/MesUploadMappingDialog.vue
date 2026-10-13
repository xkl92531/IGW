<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import MesStructCard from './MesStructCard.vue'
import { cloneReactiveValue } from '../utils/reactiveClone'
import { useI18n } from '../i18n'

const props = withDefaults(defineProps<{ modelValue: boolean; payload: any; scopeGroup?: string; editable?: boolean }>(), {
  scopeGroup: '', editable: true,
})
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; apply: [payload: any] }>()
const { t } = useI18n()
const draft = ref<any>({ fields: [], structs: [] })
const fieldFilter = ref('')
const structFilter = ref('')
const fieldPage = ref(1)
const structPage = ref(1)
const FieldPageSize = 50
const StructPageSize = 5
const fields = computed<any[]>(() => draft.value.fields || [])
const structs = computed<any[]>(() => draft.value.structs || [])
const matches = (values: unknown[], query: string) => values.some(value => String(value || '').toLowerCase().includes(query))
function structMatches(item: any, query: string): boolean {
  return matches([item.baseGroup, item.aliasName, item.kind], query) ||
    (item.members || []).some((member: any) => matches([member.tagId, member.fieldName], query)) ||
    (item.structs || []).some((child: any) => structMatches(child, query))
}
const filteredFields = computed(() => {
  const query = fieldFilter.value.trim().toLowerCase()
  return fields.value.map((item, index) => ({ item, index }))
    .filter(({ item }) => !query || matches([item.tagId, item.fieldName, item.parameterCode], query))
})
const filteredStructs = computed(() => {
  const query = structFilter.value.trim().toLowerCase()
  return structs.value.map((item, index) => ({ item, index })).filter(({ item }) => !query || structMatches(item, query))
})
function pageRows<T>(rows: T[], page: number, size: number) {
  const current = Math.min(page, Math.max(1, Math.ceil(rows.length / size)))
  return rows.slice((current - 1) * size, current * size)
}
const visibleFields = computed(() => pageRows(filteredFields.value, fieldPage.value, FieldPageSize))
const visibleStructs = computed(() => pageRows(filteredStructs.value, structPage.value, StructPageSize))
watch(fieldFilter, () => { fieldPage.value = 1 })
watch(structFilter, () => { structPage.value = 1 })
watch(() => props.modelValue, open => {
  if (!open) return
  draft.value = cloneReactiveValue(props.payload || {})
  draft.value.fields ||= []
  draft.value.structs ||= []
  fieldFilter.value = ''; structFilter.value = ''; fieldPage.value = 1; structPage.value = 1
}, { immediate: true })

function editField(item: any, key: 'fieldName' | 'parameterCode', value: unknown) {
  if (!props.modelValue || !props.editable) return
  item[key] = value
}
function remove(part: 'fields' | 'structs', index: number) {
  if (!props.modelValue || !props.editable) return
  draft.value[part].splice(index, 1)
}
function clear(part: 'fields' | 'structs') {
  if (!props.modelValue || !props.editable) return
  draft.value[part] = []
  if (part === 'fields') fieldPage.value = 1
  else structPage.value = 1
}
function apply() {
  if (!props.modelValue || !props.editable) return
  emit('apply', cloneReactiveValue(draft.value))
}
function closeDialog(done: () => void) {
  emit('update:modelValue', false)
  done()
}
</script>

<template>
  <el-dialog :model-value="modelValue" :title="t('设置上报字段和结构体')" width="min(1200px, 94vw)" top="4vh"
    append-to-body destroy-on-close data-testid="upload-mapping-dialog" :before-close="closeDialog">
    <div class="upload-mapping-body">
      <p class="mapping-hint">{{ t('应用后替换本节点的字段和结构体，取消保留原配置。自动生成报文时，参数码留空保留原始字段；填写参数码的字段和转为字典的结构体进入 parameterDic。自定义模板沿用模板内容。') }}</p>
      <section :aria-label="t('普通字段')">
        <div class="section-header">
          <h3>{{ t('普通字段') }} <span>({{ fields.length }})</span></h3>
          <el-input v-model="fieldFilter" clearable :placeholder="t('搜索完整路径、字段名或参数码')" :aria-label="t('搜索普通字段')" data-testid="field-filter" />
          <el-button :disabled="!editable || !fields.length" data-testid="clear-fields" @click="clear('fields')">{{ t('清空普通字段') }}</el-button>
        </div>
        <div class="field-list">
          <table>
            <thead><tr><th scope="col">{{ t('完整 PLC 点位路径') }}</th><th scope="col">{{ t('字段名') }}</th><th scope="col">{{ t('参数码') }}</th><th scope="col">{{ t('操作') }}</th></tr></thead>
            <tbody>
              <tr v-for="{ item, index } in visibleFields" :key="index" data-testid="mapping-field-row">
                <td><span class="field-path" :title="item.tagId">{{ item.tagId }}</span></td>
                <td><el-input :model-value="item.fieldName" :disabled="!editable" :placeholder="t('字段名(留空=用成员名)')" :aria-label="t('字段名 {path}', { path: item.tagId })" data-testid="field-name" @update:model-value="editField(item, 'fieldName', $event)" /></td>
                <td><el-input :model-value="item.parameterCode" :disabled="!editable" :placeholder="t('留空保留原样')" :aria-label="t('参数码 {path}', { path: item.tagId })" data-testid="field-code" @update:model-value="editField(item, 'parameterCode', $event)" /></td>
                <td><el-button text type="danger" :disabled="!editable" :aria-label="t('删除字段 {path}', { path: item.tagId })" data-testid="delete-field" @click="remove('fields', index)">{{ t('删') }}</el-button></td>
              </tr>
            </tbody>
          </table>
          <p v-if="!visibleFields.length" class="empty-message">{{ t('没有匹配的普通字段') }}</p>
        </div>
        <el-pagination v-if="filteredFields.length > FieldPageSize" v-model:current-page="fieldPage" :page-size="FieldPageSize" :total="filteredFields.length" layout="prev, pager, next" data-testid="field-pagination" />
      </section>
      <section :aria-label="t('结构体')">
        <div class="section-header">
          <h3>{{ t('结构体') }} <span>({{ structs.length }})</span></h3>
          <el-input v-model="structFilter" clearable :placeholder="t('搜索结构体路径、别名或成员')" :aria-label="t('搜索结构体')" data-testid="struct-filter" />
          <el-button :disabled="!editable || !structs.length" data-testid="clear-structs" @click="clear('structs')">{{ t('清空结构体') }}</el-button>
        </div>
        <el-form :disabled="!editable" @submit.prevent>
          <MesStructCard v-for="{ item, index } in visibleStructs" :key="index" :struct="item" :scope-group="scopeGroup" :editable="editable" @remove="remove('structs', index)" />
        </el-form>
        <p v-if="!visibleStructs.length" class="empty-message">{{ t('没有匹配的结构体') }}</p>
        <el-pagination v-if="filteredStructs.length > StructPageSize" v-model:current-page="structPage" :page-size="StructPageSize" :total="filteredStructs.length" layout="prev, pager, next" data-testid="struct-pagination" />
      </section>
    </div>
    <template #footer>
      <el-button data-testid="upload-mapping-cancel" @click="emit('update:modelValue', false)">{{ t('取消') }}</el-button>
      <el-button type="primary" :disabled="!editable" data-testid="upload-mapping-apply" @click="apply">{{ t('应用到上报节点') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.upload-mapping-body { max-height:calc(88vh - 140px); overflow:auto; font-size:14px; }
.mapping-hint { margin:0 0 16px; color:#606266; line-height:1.6; }
section + section { margin-top:20px; }
.section-header { display:flex; align-items:center; gap:12px; flex-wrap:wrap; margin-bottom:10px; }
h3 { margin:0; font-size:16px; font-weight:600; }
h3 span { font-size:14px; color:#606266; font-weight:400; }
.section-header .el-input { flex:1; min-width:180px; }
.field-list { max-height:min(340px, 36vh); overflow:auto; }
table { width:100%; border-collapse:collapse; table-layout:fixed; }
th { position:sticky; top:0; z-index:1; background:#f5f7fa; color:#606266; text-align:left; font-weight:500; }
th, td { box-sizing:border-box; padding:8px; border-bottom:1px solid #ebeef5; }
th:first-child { width:45%; } th:nth-child(2) { width:25%; } th:nth-child(3) { width:20%; }
.field-path { display:block; overflow-wrap:anywhere; line-height:1.5; }
.empty-message { color:#606266; margin:12px 0; }
.el-pagination { justify-content:flex-end; margin-top:12px; }
.upload-mapping-body :deep(.structcard .el-input__inner),
.upload-mapping-body :deep(.structcard .el-select__wrapper) { font-size:14px; }
.upload-mapping-body :deep(.structcard .hint),
.upload-mapping-body :deep(.structcard .lbl),
.upload-mapping-body :deep(.structcard .el-button--small) { font-size:13px; }
.upload-mapping-body :deep(.structhd > .el-select) { min-width:140px; }
@media (max-width:640px) { th:first-child { width:34%; } th:nth-child(2) { width:28%; } th:nth-child(3) { width:24%; } th,td { padding:6px 3px; } }
</style>
