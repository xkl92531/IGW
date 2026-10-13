<script setup lang="ts">
export interface ProjectContextAction {
  key?: string
  label?: string
  disabled?: boolean
  reason?: string
  gesture?: string
  danger?: boolean
  sep?: boolean
}
defineProps<{ x: number; y: number; actions: readonly ProjectContextAction[] }>()
defineEmits<{ choose: [action: ProjectContextAction] }>()
</script>

<template>
  <div class="project-context-menu" role="menu" :style="{ left: `${x}px`, top: `${y}px` }" @click.stop>
    <template v-for="(action, index) in actions" :key="action.key || index">
      <div v-if="action.sep" class="separator" />
      <button v-else type="button" role="menuitem" :class="{ danger: action.danger }"
        :disabled="action.disabled" :title="action.disabled ? action.reason : undefined" @click="$emit('choose', action)">
        <span>{{ action.label }}</span><kbd v-if="action.gesture">{{ action.gesture }}</kbd>
        <small v-if="action.disabled && action.reason">{{ action.reason }}</small>
      </button>
    </template>
  </div>
</template>

<style scoped>
.project-context-menu { position:fixed; z-index:3000; min-width:190px; padding:5px; border:1px solid #cfd9e5; border-radius:7px; background:#fff; box-shadow:0 8px 28px #24384d2b; }
button { width:100%; display:grid; grid-template-columns:minmax(0,1fr) auto; padding:7px 9px; border:0; border-radius:4px; background:transparent; text-align:left; cursor:pointer; }
button:hover:not(:disabled) { background:#edf5ff; color:#2878d0; } button:disabled { color:#aab4c0; cursor:not-allowed; }
button.danger { color:#d04444; } kbd { color:#8794a3; font:11px inherit; }
small { grid-column:1/-1; margin-top:2px; color:#9aa6b4; font-size:10px; }
.separator { height:1px; margin:4px 3px; background:#e2e8ef; }
</style>
