<script setup lang="ts">
import ScanTimingPanel from '../components/ScanTimingPanel.vue'
</script>

<template>
  <div class="performance-page">
    <div class="page-head">
      <div><h2>{{ $t('采集性能') }}</h2><p>{{ $t('查看各设备周期计划、实际完成间隔和最近周期轮询批次，不会额外读取 PLC。') }}</p></div>
    </div>
    <ScanTimingPanel />
  </div>
</template>

<style scoped>
.performance-page { min-height:100%; }.page-head { margin:0 16px 4px; }.page-head h2 { margin:0; color:#26384f; font-size:20px; }.page-head p { margin:5px 0 0; color:#748196; font-size:13px; }
</style>
