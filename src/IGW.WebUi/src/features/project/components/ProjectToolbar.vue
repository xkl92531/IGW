<script setup lang="ts">
defineProps<{ busy?: boolean; status?: string }>()
defineEmits<{ importJson: []; exportJson: []; reload: []; save: [] }>()
</script>

<template>
  <header class="project-toolbar" aria-label="工程工具栏">
    <button type="button" :disabled="busy" @click="$emit('save')">保存</button>
    <button type="button" :disabled="busy" @click="$emit('reload')">刷新</button>
    <span v-if="busy" role="status" aria-live="polite">{{ status || '正在处理…' }}</span>
    <span class="divider" />
    <button type="button" :disabled="busy" @click="$emit('importJson')">导入 JSON</button>
    <button type="button" :disabled="busy" @click="$emit('exportJson')">导出 JSON</button>
  </header>
</template>

<style scoped>
.project-toolbar { display:flex; align-items:center; gap:8px; min-height:42px; padding:0 12px; border-bottom:1px solid #dce5ef; background:#fff; }
button { min-height:30px; padding:0 13px; border:1px solid #cfd9e5; border-radius:5px; background:#fff; color:#26384a; cursor:pointer; }
button:hover:not(:disabled) { color:#2878d0; border-color:#83b7ef; background:#f4f9ff; }
button:disabled { opacity:.5; cursor:not-allowed; }
.divider { width:1px; height:20px; margin:0 3px; background:#dce5ef; }
</style>
