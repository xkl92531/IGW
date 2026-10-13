<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { uiActivities } from '../utils/requestActivity'
import { useI18n } from '../i18n'
const { t } = useI18n()
const visible = ref(false), now = ref(Date.now())
let delay: ReturnType<typeof setTimeout> | undefined
let timer: ReturnType<typeof setInterval> | undefined
function stop() { clearTimeout(delay); clearInterval(timer); delay = undefined; timer = undefined; visible.value = false }
watch(() => uiActivities.value.length, count => {
  if (!count) { stop(); return }
  if (delay || visible.value) return
  delay = setTimeout(() => {
    visible.value = true; now.value = Date.now()
    timer = setInterval(() => { now.value = Date.now() }, 1000)
  }, 250)
}, { immediate: true })
onBeforeUnmount(stop)
const first = computed(() => uiActivities.value[0])
const seconds = computed(() => first.value ? Math.max(0, Math.floor((now.value - first.value.startedAt) / 1000)) : 0)
</script>
<template>
  <div v-if="visible && first" class="request-activity" role="status" aria-live="polite" aria-atomic="true">
    <i class="activity-spinner" aria-hidden="true" /><span>{{ t(first.label) }}</span>
    <small v-if="uiActivities.length > 1">{{ t('另有 {count} 项处理中', { count: uiActivities.length - 1 }) }}</small>
    <small v-if="seconds >= 3">{{ t('已等待 {seconds} 秒', { seconds }) }}</small>
  </div>
</template>
<style scoped>
.request-activity{position:fixed;top:5px;left:50%;transform:translateX(-50%);z-index:15000;display:flex;align-items:center;gap:8px;max-width:85vw;box-sizing:border-box;padding:8px 14px;border:1px solid #b8d6f4;border-radius:8px;background:#edf6ff;color:#175b97;box-shadow:0 2px 10px #1232;font-size:13px;pointer-events:none}.request-activity small{color:#5b7390}.activity-spinner{display:inline-block;width:14px;height:14px;flex:none;border:2px solid #b9d8f5;border-top-color:#2675ba;border-radius:50%;animation:activity-spin .8s linear infinite}@keyframes activity-spin{to{transform:rotate(360deg)}}@media(prefers-reduced-motion:reduce){.activity-spinner{animation:none}}
</style>
