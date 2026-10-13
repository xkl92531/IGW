<script setup lang="ts">
defineProps<{ title?: string; path?: string; description?: string | null }>()
</script>

<template>
  <aside class="project-property-panel">
    <header><strong>{{ title || '属性' }}</strong><small v-if="description">{{ description }}</small><span v-if="path" :title="path">{{ path }}</span></header>
    <div class="property-content"><slot><p>选择节点或点位查看属性</p></slot></div>
  </aside>
</template>

<style scoped>
.project-property-panel { height:100%; overflow:auto; background:#fff; }
header { display:flex; flex-direction:column; gap:4px; padding:14px 16px; border-bottom:1px solid #dce5ef; }
header small { color:#66788d; } header span { overflow:hidden; color:#8392a5; font-size:11px; white-space:nowrap; text-overflow:ellipsis; }
.property-content { padding:16px; } .property-content p { color:#8392a5; }
</style>
