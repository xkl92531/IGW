<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, type RuntimeLogDirectoryEntryDto, type RuntimeLogDirectoryListingDto } from '../api'
import { useI18n } from '../i18n'

const props = defineProps<{ modelValue: boolean; initialDirectory: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; select: [path: string] }>()
const { t } = useI18n()
// 日志目录保存时要求所在磁盘至少剩余 1 GiB(见后端 LogStoragePreflight)。
const MIN_FREE_BYTES = 1024 ** 3
const loading = ref(false)
const loaded = ref(false)
const error = ref('')
const notice = ref('')
const path = ref('')
const listing = ref<RuntimeLogDirectoryListingDto>({ currentPath: '', parentPath: null, directories: [], truncated: false })
let sequence = 0
// 当前路径为空表示正在显示磁盘列表；磁盘列表本身不是一个可选目录。
const showingDrives = computed(() => loaded.value && !listing.value.currentPath)
const canSelect = computed(() => !loading.value && !error.value && !!listing.value.currentPath && path.value === listing.value.currentPath)

function readError(reason: any): string {
  return reason?.response?.data?.error || reason?.message || t('目录读取失败')
}

async function browse(target: string, fallbackToDrives = false): Promise<void> {
  const request = ++sequence
  loading.value = true
  error.value = ''
  notice.value = ''
  path.value = target
  try {
    const result = await api.runtimeLogDirectories(target)
    if (request !== sequence) return
    listing.value = result
    loaded.value = true
    path.value = result.currentPath
  } catch (reason: any) {
    if (request !== sequence) return
    // 打开对话框时的初始目录可能已被删除或是手填的未创建路径；此时直接给出磁盘列表，
    // 而不是让用户对着一条报错无处可点。用户主动打开的路径仍然如实报错。
    if (fallbackToDrives && target.trim()) {
      const failed = target
      await browse('')
      if (sequence === request + 1 && !error.value)
        notice.value = t('无法打开 {path}，已显示磁盘列表：{reason}', { path: failed, reason: readError(reason) })
      return
    }
    error.value = readError(reason)
  } finally { if (request === sequence) loading.value = false }
}

function select() {
  if (!canSelect.value) return
  emit('select', listing.value.currentPath)
  emit('update:modelValue', false)
}

function formatBytes(bytes: number): string {
  if (bytes >= 1024 ** 4) return `${(bytes / 1024 ** 4).toFixed(1)} TB`
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`
  return `${Math.round(bytes / 1024 ** 2)} MB`
}

function capacity(entry: RuntimeLogDirectoryEntryDto): string {
  if (entry.freeBytes == null) return ''
  return entry.totalBytes
    ? t('可用 {free} / 共 {total}', { free: formatBytes(entry.freeBytes), total: formatBytes(entry.totalBytes) })
    : t('可用 {free}', { free: formatBytes(entry.freeBytes) })
}

const lowSpace = (entry: RuntimeLogDirectoryEntryDto) => entry.freeBytes != null && entry.freeBytes < MIN_FREE_BYTES

watch(() => props.modelValue, (open) => {
  if (open) browse(props.initialDirectory, true)
  else { sequence++; loading.value = false }
}, { immediate: true })

</script>

<template>
  <el-dialog :model-value="modelValue" :title="t('选择日志文件夹')" width="640px" append-to-body
    @update:model-value="emit('update:modelValue', $event)">
    <p class="directory-hint">{{ t('这里显示运行 IGW 的网关电脑上的文件夹。') }}</p>
    <div class="directory-toolbar">
      <el-button :type="showingDrives ? 'primary' : undefined" :disabled="loading" @click="browse('')">{{ t('磁盘列表') }}</el-button>
      <el-button :disabled="!listing.currentPath || loading" @click="browse(listing.parentPath || '')">{{ t('上一级') }}</el-button>
    </div>
    <div class="directory-address">
      <el-input v-model="path" :placeholder="t('网关电脑上的绝对路径')" :aria-label="t('文件夹路径')" @keyup.enter="browse(path)" />
      <el-button :loading="loading" @click="browse(path)">{{ t('打开') }}</el-button>
    </div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-alert v-else-if="notice" :title="notice" type="warning" :closable="false" show-icon />
    <div v-loading="loading" class="directory-list" role="list" :aria-label="showingDrives ? t('磁盘列表') : t('文件夹')">
      <button v-for="directory in listing.directories" :key="directory.path" type="button" role="listitem"
        class="directory-row" :class="{ 'low-space': showingDrives && lowSpace(directory) }" :disabled="loading"
        @click="browse(directory.path)">
        <span aria-hidden="true">{{ showingDrives ? '💽' : '📁' }}</span>
        <span>{{ directory.name }}</span>
        <span v-if="showingDrives && directory.label" class="directory-label">{{ directory.label }}</span>
        <span v-if="showingDrives && capacity(directory)" class="directory-capacity"
          :title="lowSpace(directory) ? t('剩余空间不足 1 GB，不能作为日志目录') : undefined">{{ capacity(directory) }}</span>
        <span class="directory-arrow">›</span>
      </button>
      <div v-if="!loading && !error && !listing.directories.length" class="directory-empty">
        {{ showingDrives ? t('没有找到可用的磁盘') : t('此目录没有子文件夹，可直接选择当前目录') }}
      </div>
    </div>
    <small v-if="listing.truncated">{{ t('子文件夹较多，仅显示前 500 个；可填写完整路径打开。') }}</small>
    <small v-else-if="showingDrives">{{ t('请先进入一个磁盘，再选择其中的文件夹。') }}</small>
    <template #footer>
      <el-button @click="emit('update:modelValue', false)">{{ t('取消') }}</el-button>
      <el-button type="primary" :disabled="!canSelect" @click="select">{{ t('使用此目录') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.directory-hint { margin: 0 0 12px; color: #667085; font-size: 13px; }
.directory-toolbar, .directory-address { display: flex; gap: 8px; margin-bottom: 10px; }
.directory-list { height: 280px; overflow-y: auto; margin-top: 10px; border: 1px solid #e1e7ef; border-radius: 6px; }
.directory-row { display: flex; align-items: center; gap: 10px; width: 100%; padding: 10px 12px; border: 0; border-bottom: 1px solid #f2f4f7; background: #fff; text-align: left; cursor: pointer; color: #344054; }
.directory-row:hover { background: #eff6ff; }
.directory-label { color: #667085; }
.directory-capacity { margin-left: auto; color: #667085; font-size: 12px; }
.directory-capacity + .directory-arrow { margin-left: 8px; }
.directory-arrow { margin-left: auto; color: #98a2b3; }
.directory-row.low-space .directory-capacity { color: #d92d20; }
.directory-empty { padding: 32px 16px; text-align: center; color: #98a2b3; }
small { display: block; margin-top: 8px; color: #667085; }
</style>
