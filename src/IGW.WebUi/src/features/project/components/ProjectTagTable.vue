<script setup lang="ts">
import type { ProjectTagRowDto } from '../types'

const props = defineProps<{ rows: readonly ProjectTagRowDto[]; selectedIds: readonly string[]; total?: number; loading?: boolean }>()
const emit = defineEmits<{ toggle: [id: string, selected: boolean]; context: [event: MouseEvent, row: ProjectTagRowDto]; edit: [row: ProjectTagRowDto] }>()
function selected(id: string) { return props.selectedIds.includes(id) }
</script>

<template>
  <section class="tag-table-wrap" :aria-busy="loading">
    <table class="project-tag-table">
      <thead><tr><th class="check"></th><th>名称</th><th>说明</th><th>实际路径</th><th>PLC 地址</th><th>数据类型</th><th>权限</th></tr></thead>
      <tbody>
        <tr v-for="row in rows" :key="row.id" :data-row-id="row.id" :class="{ 'is-selected': selected(row.id) }"
          :title="`${row.actualPath}${row.description ? `\n${row.description}` : ''}`"
          @click="emit('toggle', row.id, !selected(row.id))" @dblclick="emit('edit', row)" @contextmenu.prevent="emit('context', $event, row)">
          <td class="check"><input type="checkbox" :checked="selected(row.id)" @click.stop @change="emit('toggle', row.id, ($event.target as HTMLInputElement).checked)" /></td>
          <td>{{ row.name }}</td><td>{{ row.description || '—' }}</td><td>{{ row.actualPath }}</td>
          <td>{{ row.address }}</td><td>{{ row.dataType }}</td><td>{{ row.access }}</td>
        </tr>
      </tbody>
    </table>
    <footer>当前 {{ rows.length }} 条<span v-if="total !== undefined"> / 共 {{ total }} 条</span></footer>
  </section>
</template>

<style scoped>
.tag-table-wrap { height:100%; overflow:auto; background:#fff; }
.project-tag-table { width:100%; border-collapse:separate; border-spacing:0; table-layout:fixed; font-size:13px; }
th { position:sticky; top:0; z-index:1; padding:9px 10px; border-bottom:1px solid #dce5ef; background:#f6f8fb; color:#52657a; text-align:left; }
td { overflow:hidden; padding:8px 10px; border-bottom:1px solid #edf1f5; white-space:nowrap; text-overflow:ellipsis; }
tbody tr { cursor:pointer; }
tbody tr:hover { background:#f5f9fe; }
tbody tr.is-selected { background:#eaf3ff; box-shadow:inset 3px 0 #409eff; }
.check { width:34px; text-align:center; }
footer { position:sticky; bottom:0; padding:7px 12px; border-top:1px solid #dce5ef; background:#fafcfe; color:#718399; text-align:right; }
</style>
