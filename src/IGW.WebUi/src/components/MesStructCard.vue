<script setup lang="ts">
import { computed, ref, useId } from 'vue'
import MesParameterPicker from './MesParameterPicker.vue'
import { useI18n } from '../i18n'

/**
 * 字段/结构映射里的一个结构体。结构体里可以再套子结构体(对象或对象数组)，和 DB 里的层级一致：
 * 顶层的基准组写工程里的完整路径；子结构体写相对上一层元素的路径(如 GlueData、CellGlueData)。
 */
// editable=false：只读账号，只能展开查看，增删按钮隐藏(输入框由外层 el-form 统一禁用)
const props = withDefaults(defineProps<{ struct: any; nested?: boolean; scopeGroup?: string; editable?: boolean }>(), { nested: false, scopeGroup: '', editable: true })
const emit = defineEmits<{ change: []; remove: [] }>()
const { t } = useI18n()
const dictionaryHintId = useId()

const PageSize = 50
const page = ref(1)
const collapsed = ref(props.nested)   // 子结构体默认收起，点标题展开
const members = computed(() => props.struct.members || [])
const children = computed(() => props.struct.structs || [])
const pageCount = computed(() => Math.max(1, Math.ceil(members.value.length / PageSize)))
const visibleMembers = computed(() => {
  const current = Math.min(page.value, pageCount.value)
  const start = (current - 1) * PageSize
  return members.value.slice(start, start + PageSize).map((item: any, index: number) => ({ item, index: start + index }))
})
const summary = computed(() => t('{members} 个成员 · {children} 个子结构体', { members: members.value.length, children: children.value.length }))

function changed() { emit('change') }
function setDictionary(event: Event) {
  if (!props.editable) return
  props.struct.asDictionary = (event.target as HTMLInputElement).checked
  changed()
}
function addMember() {
  (props.struct.members ||= []).push({ tagId: '', fieldName: '' })
  page.value = Math.ceil(props.struct.members.length / PageSize); changed()
}
function removeMember(index: number) { props.struct.members.splice(index, 1); changed() }
function addChild() {
  (props.struct.structs ||= []).push({ kind: 'Object', baseGroup: '', aliasName: '', countMode: 'Fixed', startIndex: 0, fixedCount: 1, members: [], structs: [] })
  collapsed.value = false; changed()
}
function removeChild(index: number) { props.struct.structs.splice(index, 1); changed() }
</script>

<template>
  <div class="structcard" :class="{ nested }" data-testid="struct-card">
    <div class="structhd">
      <!-- 展开/收起不算修改：包一层不禁用的表单，只读时也能点 -->
      <el-form :disabled="false" style="display:contents" @submit.prevent>
        <el-button size="small" text class="fold" :aria-label="collapsed ? t('展开') : t('收起')" @click="collapsed = !collapsed">{{ collapsed ? '▸' : '▾' }}</el-button>
      </el-form>
      <el-select v-model="struct.kind" size="small" style="width:110px" @change="changed">
        <el-option value="List" :label="t('值数组')" /><el-option value="Object" :label="t('对象')" /><el-option value="Array" :label="t('数组(套对象)')" />
      </el-select>
      <el-input v-model="struct.aliasName" :placeholder="t('后台 JSON 字段名')" size="small" style="width:150px" @input="changed" />
      <label class="dictionary-toggle" data-testid="struct-dictionary-toggle">
        <input type="checkbox" :checked="!!struct.asDictionary" :disabled="!editable"
          :aria-describedby="struct.asDictionary ? dictionaryHintId : undefined" @change="setDictionary" />
        {{ t('转为字典') }}
      </label>
      <small v-if="collapsed" class="summary">{{ summary }}</small>
      <span class="grow" />
      <el-button v-if="editable" size="small" text type="danger" @click="emit('remove')">{{ t(nested ? '删子结构体' : '删结构体') }}</el-button>
    </div>
    <div v-if="struct.asDictionary" :id="dictionaryHintId" class="hint">{{ t('自动生成报文时，成员值进入根 parameterDic，以完整 PLC 点位路径为键；无需填写成员参数码，JSON 别名和结构层级不作为字典键。') }}</div>
    <template v-if="!collapsed">
      <div class="structrow">
        <span class="lbl">{{ t(nested ? '相对路径' : '基准组') }}</span>
        <el-input v-if="nested" v-model="struct.baseGroup" :placeholder="t('相对上一层的路径(如 GlueData)')" size="small" @input="changed" />
        <MesParameterPicker v-else v-model="struct.baseGroup" :scope-group="scopeGroup" kind="group" access="read" :placeholder="t('基准组路径(如 通道.设备.OCV)')" size="small" @change="changed" />
      </div>
      <div v-if="!nested" class="hint">{{ t('JSON 名称只控制发给后台的字段名；PLC/QC 始终按基准组和成员名读取。') }}</div>
      <div v-if="struct.kind === 'Array'" class="structrow">
        <span class="lbl">{{ t('模式') }}</span>
        <el-select v-model="struct.countMode" size="small" style="width:110px" @change="changed">
          <el-option value="Enum" :label="t('枚举子组')" /><el-option value="Fixed" :label="t('固定')" /><el-option value="Tag" :label="t('按点取')" />
        </el-select>
        <template v-if="struct.countMode === 'Enum'">
          <span class="lbl">{{ t('起始') }}</span>
          <el-input-number v-model="struct.startIndex" :min="0" size="small" controls-position="right" style="width:78px" @change="changed" />
          <span class="lbl">{{ t('数量') }}</span>
          <el-input-number v-model="struct.fixedCount" :min="1" size="small" controls-position="right" style="width:92px" @change="changed" />
        </template>
        <template v-else>
          <el-input-number v-if="struct.countMode !== 'Tag'" v-model="struct.fixedCount" :min="1" size="small" controls-position="right" style="width:92px" @change="changed" />
          <MesParameterPicker v-else v-model="struct.countTagId" :scope-group="scopeGroup" kind="tag" access="read" :placeholder="t('数量点')" size="small" @change="changed" />
          <span class="lbl">{{ t('起始') }}</span>
          <el-input-number v-model="struct.startIndex" :min="0" size="small" controls-position="right" style="width:78px" @change="changed" />
        </template>
      </div>
      <div v-if="struct.kind === 'Array' && struct.countMode === 'Enum'" class="hint">{{ t('自动扫描「名字[下标]」或西门子导入的「名字_下标」子组，仅上报起始下标起的指定数量。') }}</div>
      <div class="hint">{{ t('成员(相对基准组;值数组=按顺序取值进数组 / 对象=键值 / 数组=每元素成员):') }}</div>
      <div v-for="{ item: m, index: mi } in visibleMembers" :key="'m' + mi" class="crow struct-member-row">
        <el-input v-model="m.tagId" :placeholder="t('成员名(如 Code1)')" size="small" style="flex:1;min-width:0" @input="changed" />
        <el-input v-if="struct.kind !== 'List'" v-model="m.fieldName" :placeholder="t('字段名(留空=用成员名)')" size="small" style="width:120px" @input="changed" />
        <el-button v-if="editable" size="small" text type="danger" @click="removeMember(mi)">{{ t('删') }}</el-button>
      </div>
      <div v-if="members.length > PageSize" class="mapping-pagination">
        <span>{{ t('共 {count} 个成员', { count: members.length }) }}</span>
        <el-pagination v-model:current-page="page" size="small" layout="prev, pager, next" :page-size="PageSize" :total="members.length" />
      </div>
      <el-button v-if="editable" size="small" text @click="addMember">{{ t('+ 成员') }}</el-button>
      <el-button v-if="editable && struct.kind !== 'List'" size="small" text type="primary" @click="addChild">{{ t('+ 子结构体') }}</el-button>
      <!-- 子结构体一层层套着，和 DB/UDT 里的结构一致 -->
      <div v-if="children.length && struct.kind !== 'List'" class="children">
        <MesStructCard v-for="(child, ci) in children" :key="'c' + ci" :struct="child" nested :scope-group="scopeGroup" :editable="editable"
          @change="changed" @remove="removeChild(ci)" />
      </div>
    </template>
  </div>
</template>

<style scoped>
.structcard { border:1px solid #dfe6ee; border-radius:8px; padding:8px; margin:6px 0; background:#fbfcfe; }
.structcard.nested { background:#fff; border-style:dashed; margin-left:4px; }
.structhd, .structrow { display:flex; align-items:center; gap:6px; margin-bottom:6px; flex-wrap:wrap; }
.structrow .el-input { flex:1; min-width:0; }
.structhd .grow { flex:1; }
.fold { padding:0 4px; min-width:20px; }
.dictionary-toggle { display:inline-flex; align-items:center; gap:4px; font-size:12px; color:#606266; }
.dictionary-toggle input { margin:0; accent-color:var(--el-color-primary); }
.summary { color:#8793a1; font-size:12px; }
.lbl { color:#6f7d8f; font-size:12px; }
.hint { color:#8793a1; font-size:12px; margin:2px 0 4px; }
.crow { display:flex; align-items:center; gap:6px; margin-bottom:4px; }
.children { margin-top:6px; padding-left:10px; border-left:2px solid #e6edf5; }
.mapping-pagination { display:flex; align-items:center; justify-content:space-between; margin:4px 0; color:#8793a1; font-size:12px; }
</style>
