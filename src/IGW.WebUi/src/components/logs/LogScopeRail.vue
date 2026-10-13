<script setup lang="ts">
import type { LogCategory } from '../../utils/logCenter'
import { useI18n } from '../../i18n'

defineProps<{ modelValue: LogCategory; counts?: Partial<Record<LogCategory, number>> }>()
const emit = defineEmits<{ 'update:modelValue': [value: LogCategory] }>()
const { t } = useI18n()
const scopes: Array<{ id: LogCategory; label: string; mark: string }> = [
  { id: 'all', label: '全部事件', mark: '◎' },
  { id: 'collection', label: '采集记录', mark: '↻' },
  { id: 'interface', label: '接口往来', mark: '⇄' },
  { id: 'state', label: '状态变化', mark: '◇' },
  { id: 'performance', label: '性能分析', mark: '∿' },
  { id: 'system', label: '系统运行', mark: '◆' },
]
</script>

<template>
  <nav class="scope-rail" :aria-label="t('日志范围')">
    <div class="rail-caption">{{ t('日志范围') }}</div>
    <button v-for="scope in scopes" :key="scope.id" type="button" class="scope-choice"
      :class="{ selected: modelValue === scope.id }" :aria-current="modelValue === scope.id ? 'page' : undefined"
      @click="emit('update:modelValue', scope.id)">
      <span class="scope-mark" aria-hidden="true">{{ scope.mark }}</span>
      <span>{{ t(scope.label) }}</span>
      <b v-if="counts?.[scope.id] !== undefined">{{ counts[scope.id] }}</b>
    </button>
  </nav>
</template>

<style scoped>
.scope-rail { display:flex; flex-direction:column; gap:5px; padding:14px 10px; border:1px solid #dfe6ef; border-radius:12px; background:#fff; }
.rail-caption { padding:2px 10px 8px; color:#7a8798; font-size:11px; font-weight:700; letter-spacing:.12em; text-transform:uppercase; }
.scope-choice { display:grid; grid-template-columns:24px 1fr auto; align-items:center; gap:8px; min-height:40px; padding:7px 10px; border:1px solid transparent; border-radius:8px; background:transparent; color:#526174; font:inherit; font-size:13px; text-align:left; cursor:pointer; }
.scope-choice:hover { background:#f3f7fc; color:#1a5fb4; }.scope-choice.selected { border-color:#bad2ed; background:#eaf3fd; color:#164f91; font-weight:700; }
.scope-mark { display:grid; width:22px; height:22px; place-items:center; border-radius:6px; background:#eef2f7; font-size:14px; }.selected .scope-mark { background:#fff; }
.scope-choice b { min-width:18px; color:#7a8798; font-size:11px; text-align:right; }
@media (max-width:1100px) { .scope-rail { flex-direction:row; overflow-x:auto; padding:8px; }.rail-caption { display:none; }.scope-choice { grid-template-columns:20px auto; white-space:nowrap; }.scope-choice b { display:none; } }
</style>
