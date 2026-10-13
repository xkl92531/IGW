<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { normalizeArrayDef, parseIntegerVector, type ArrayDef } from '../utils/arrayTags'
import { useI18n } from '../i18n'

const model = defineModel<any>({ required: true })
// editable=false：只看数组配置，开关和字段都禁用
const props = withDefaults(defineProps<{ editable?: boolean }>(), { editable: true })
const { t } = useI18n()
const dimensionsText = ref('1')
const lowerBoundsText = ref('0')

const enabled = computed({
  get: () => !!model.value?.array,
  set: (value: boolean) => {
    if (!props.editable) return
    const next = { ...model.value }
    if (value) {
      next.isArray = true
      next.array = normalizeArrayDef({
        elementType: next.dataType || 'int16', dimensions: [1], lowerBounds: [0],
      })
    } else {
      next.isArray = false
      next.array = null
    }
    model.value = next
  },
})

watch(() => model.value?.array, (array) => {
  if (!array) return
  dimensionsText.value = (array.dimensions || [1]).join(', ')
  lowerBoundsText.value = (array.lowerBounds?.length ? array.lowerBounds : (array.dimensions || [1]).map(() => 0)).join(', ')
}, { immediate: true, deep: true })

watch(() => model.value?.dataType, (dataType) => {
  if (!model.value?.array || model.value.array.elementType === 'struct') return
  updateArray({ elementType: dataType })
})

function updateArray(change: Partial<ArrayDef>) {
  if (!model.value?.array) return
  const nextArray = normalizeArrayDef({ ...model.value.array, ...change })
  model.value = { ...model.value, isArray: true, dataType: nextArray.elementType, array: nextArray }
}

function applyShape() {
  if (!props.editable) return
  try {
    const dimensions = parseIntegerVector(dimensionsText.value, t('维度'))
    if (dimensions.some((value) => value <= 0)) throw new Error(t('维度必须大于零'))
    const lowerBounds = parseIntegerVector(lowerBoundsText.value, t('下界'))
    if (dimensions.length !== lowerBounds.length) throw new Error(t('维度与下界数量必须一致'))
    updateArray({ dimensions, lowerBounds })
  } catch (error: any) {
    ElMessage.warning(error?.message || t('数组形状无效'))
  }
}

const structureFields = computed(() => model.value?.array?.struct?.fields || [])
</script>

<template>
  <div class="array-editor">
    <div class="array-mode">
      <span>{{ $t('逻辑数组标记') }}</span>
      <el-switch v-model="enabled" :disabled="!editable" />
      <small>{{ $t('一个数组只注册一个点位，QC 按切片查看，不逐元素轮询。') }}</small>
    </div>
    <template v-if="enabled">
      <el-form :disabled="!editable" label-width="120px" size="small">
        <el-form-item :label="$t('元素类型')">
          <el-input :model-value="model.array.elementType" disabled />
        </el-form-item>
        <el-form-item :label="$t('维度')">
          <el-input v-model="dimensionsText" :placeholder="$t('例如 10 或 2, 3')" @change="applyShape" />
        </el-form-item>
        <el-form-item :label="$t('PLC 下界')">
          <el-input v-model="lowerBoundsText" :placeholder="$t('例如 0 或 1, -2')" @change="applyShape" />
        </el-form-item>
        <el-form-item :label="$t('元素步长(字节)')">
          <el-input-number v-model="model.array.elementStrideBytes" :min="0" :max="1048576" controls-position="right" style="width:100%" />
        </el-form-item>
        <el-form-item v-if="model.array.elementType === 'bool'" :label="$t('元素步长(位)')">
          <el-input-number v-model="model.array.elementStrideBits" :min="0" :max="8" controls-position="right" style="width:100%" />
        </el-form-item>
        <el-form-item v-if="['string', 'wString'].includes(model.array.elementType)" :label="$t('字符串容量')">
          <el-input-number v-model="model.array.stringLength" :min="1" :max="32767" controls-position="right" style="width:100%" />
        </el-form-item>
      </el-form>
      <div v-if="model.array.struct" class="struct-layout">
        <div class="struct-head">
          <b>{{ $t('导入的结构体布局（只读）') }}</b>
          <span>{{ model.array.struct.name }} · {{ model.array.struct.sizeBytes }} {{ $t('字节') }} · {{ structureFields.length }} {{ $t('个成员') }}</span>
        </div>
        <el-table :data="structureFields" size="small" max-height="180" border>
          <el-table-column prop="name" :label="$t('成员')" min-width="130" show-overflow-tooltip />
          <el-table-column prop="jsonName" :label="$t('JSON 名称')" min-width="120" show-overflow-tooltip />
          <el-table-column prop="dataType" :label="$t('类型')" width="90" />
          <el-table-column prop="byteOffset" :label="$t('字节偏移')" width="86" />
        </el-table>
      </div>
      <div class="array-source" v-if="model.array.sourceShape">{{ $t('来源') }}：{{ model.array.sourceShape }}</div>
    </template>
  </div>
</template>

<style scoped>
.array-editor { border: 1px solid #dfe6ee; border-radius: 8px; padding: 10px 12px; background: #f8fbff; }
.array-mode { display: grid; grid-template-columns: auto auto 1fr; align-items: center; gap: 10px; color: #26384f; }
.array-mode small { color: #7b8796; line-height: 1.4; }
.array-editor :deep(.el-form) { margin-top: 10px; }
.struct-layout { margin-top: 8px; }
.struct-head { display: flex; justify-content: space-between; gap: 12px; margin-bottom: 7px; color: #43546a; font-size: 12px; }
.struct-head span, .array-source { color: #8793a3; }
.array-source { margin-top: 7px; font-size: 11px; }
</style>
