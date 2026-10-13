<script setup lang="ts">
defineProps<{ count: number; busy?: boolean; canPaste?: boolean }>()
defineEmits<{ copy: []; cut: []; paste: []; clear: [] }>()
</script>

<template>
  <div v-if="count || canPaste" class="project-batch-bar">
    <strong v-if="count">已选 {{ count }} 项</strong>
    <button :disabled="!count || busy" @click="$emit('copy')">复制</button>
    <button :disabled="!count || busy" @click="$emit('cut')">剪切</button>
    <button :disabled="!canPaste || busy" @click="$emit('paste')">粘贴</button>
    <button v-if="count" :disabled="busy" @click="$emit('clear')">取消选择</button>
  </div>
</template>

<style scoped>
.project-batch-bar { display:flex; align-items:center; gap:7px; padding:7px 12px; border-bottom:1px solid #bad5f1; background:#eef6ff; color:#34516f; }
button { border:1px solid #aec9e6; border-radius:4px; background:#fff; cursor:pointer; } button:disabled { opacity:.5; }
</style>
