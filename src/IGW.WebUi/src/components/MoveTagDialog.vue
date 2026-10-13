<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { projectApi } from '../features/project/api'
import { useI18n } from '../i18n'
import { canEdit } from '../auth'

/**
 * 把配错位置的点位挪到正确的设备/组下面。
 *
 * 只改父节点（走工程的 moveEntities 增量变更），地址原样保留——同一个地址在两台设备上都是合法写法，
 * 该不该改由现场按 PLC 程序决定；挪完若仍读不到，监控中心会继续报出新的原因。
 */
const props = defineProps<{ modelValue: boolean; tagPath: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; moved: [] }>()
const { t } = useI18n()

type Node = { id: string; name: string; path: string; kind: 'channel' | 'device' | 'group'; children?: Node[]; loaded?: boolean }

const loading = ref(false)
const saving = ref(false)
const error = ref('')
const scope = ref<'tag' | 'group'>('tag')
const source = ref<any>(null)
const tree = ref<Node[]>([])
const targetId = ref('')
const targetPath = ref('')

// 移动走工程变更，只有「工程配置」可修改的账号才显示保存按钮(后端同样按 project 判断)
const editable = computed(() => canEdit('project'))
const open = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value),
})

/** 要移动的是这一个点位，还是它所在的整个组。 */
const movingLabel = computed(() => scope.value === 'tag'
  ? props.tagPath
  : source.value?.parentPath || props.tagPath)

const canSubmit = computed(() => !!targetId.value && !!source.value?.found
  && (scope.value === 'tag' ? source.value.kind === 'tag' : source.value.parentKind === 'group'))

// immediate：父组件可能以「已打开」状态挂载本弹窗，那种情况下也要把工程结构载进来
watch(() => props.modelValue, async (visible) => {
  if (!visible) return
  error.value = ''
  scope.value = 'tag'
  targetId.value = ''
  targetPath.value = ''
  loading.value = true
  try {
    const [matches, shell] = await Promise.all([
      projectApi.resolvePaths([props.tagPath]),
      projectApi.shell(),
    ])
    source.value = matches[0]
    if (!source.value?.found) error.value = t('工程里找不到这个点位，可能已被删除或改名')
    tree.value = (shell.project?.channels || []).map((channel: any) => ({
      id: channel.id,
      name: channel.name,
      path: channel.name,
      kind: 'channel' as const,
      loaded: true,
      children: (channel.devices || []).map((device: any) => ({
        id: device.id,
        name: device.name,
        path: `${channel.name}.${device.name}`,
        kind: 'device' as const,
      })),
    }))
  } catch (reason: any) {
    error.value = reason?.message || t('载入工程结构失败')
  } finally {
    loading.value = false
  }
}, { immediate: true })

/** 懒加载：展开设备/组时才取它的下级组（不取点位，移动目标只可能是设备或组）。 */
async function loadChildren(node: Node) {
  if (node.kind === 'channel' || node.loaded) return
  try {
    const children = await projectApi.lazyChildren(node.kind === 'device' ? 'device' : 'group', node.id)
    node.children = (children.groups || []).map((group: any) => ({
      id: group.id,
      name: group.name,
      path: `${node.path}.${group.name}`,
      kind: 'group' as const,
    }))
    node.loaded = true
  } catch (reason: any) {
    ElMessage.error(reason?.message || t('载入下级分组失败'))
  }
}

function pick(node: Node) {
  if (node.kind === 'channel') return   // 点位只能挂在设备或组下面
  targetId.value = node.id
  targetPath.value = node.path
}

async function submit() {
  if (!canSubmit.value) return
  saving.value = true
  error.value = ''
  try {
    const moving = scope.value === 'tag'
      ? { kind: 'tag' as const, id: source.value.id }
      : { kind: 'group' as const, id: source.value.parentId }
    await projectApi.mutate({
      kind: 'moveEntities',
      entityKind: moving.kind,
      entityIds: [moving.id],
      targetId: targetId.value,
      documentJson: null,
    })
    ElMessage.success(t('已移动到 {path}', { path: targetPath.value }))
    emit('moved')
    open.value = false
  } catch (reason: any) {
    error.value = reason?.response?.data?.error || reason?.message || t('移动失败')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <el-dialog v-model="open" :title="$t('移动点位')" width="620px" class="movedialog">
    <div v-if="loading" class="hint">{{ $t('正在载入工程结构…') }}</div>
    <template v-else>
      <el-alert v-if="error" type="error" :title="error" :closable="false" show-icon class="movealert" />

      <div class="moverow">
        <span class="movelabel">{{ $t('移动内容') }}</span>
        <el-radio-group v-model="scope" size="small">
          <el-radio-button value="tag">{{ $t('仅这一个点位') }}</el-radio-button>
          <el-radio-button value="group" :disabled="source?.parentKind !== 'group'">
            {{ $t('它所在的整个分组') }}
          </el-radio-button>
        </el-radio-group>
      </div>
      <div class="moverow">
        <span class="movelabel">{{ $t('当前位置') }}</span>
        <code class="movepath">{{ movingLabel }}</code>
      </div>

      <div class="movetreewrap">
        <div class="movelabel">{{ $t('移动到（选择设备或分组）') }}</div>
        <el-tree :data="tree" node-key="id" :props="{ label: 'name', children: 'children' }"
                 :expand-on-click-node="false" class="movetree"
                 @node-expand="(node: any) => loadChildren(node)">
          <template #default="{ data }">
            <span class="movenode" :class="{ picked: data.id === targetId, disabled: data.kind === 'channel' }"
                  @click="pick(data)">
              <span class="movenodename">{{ data.name }}</span>
              <small>{{ data.kind === 'channel' ? $t('通道') : data.kind === 'device' ? $t('设备') : $t('分组') }}</small>
            </span>
          </template>
        </el-tree>
      </div>

      <div v-if="targetPath" class="movetarget">
        {{ $t('目标位置') }}<code class="movepath">{{ targetPath }}</code>
      </div>
      <div class="hint">{{ $t('只改父节点，点位地址保持不变；保存走增量变更，不会整包重写工程。') }}</div>
    </template>

    <template #footer>
      <el-button @click="open = false">{{ $t('取消') }}</el-button>
      <el-button v-if="editable" type="primary" :loading="saving" :disabled="!canSubmit" @click="submit">
        {{ $t('移动并保存') }}
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.movealert { margin-bottom: 10px; }
.moverow { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.movelabel { min-width: 132px; color: #526174; font-size: 13px; font-weight: 600; }
.movepath { padding: 2px 6px; border-radius: 4px; background: #f3f7fc; color: #164f91; font-size: 12px; word-break: break-all; }
.movetreewrap { margin-top: 6px; }
.movetree { max-height: 280px; margin-top: 6px; padding: 6px; overflow: auto; border: 1px solid #dfe6ef; border-radius: 8px; }
.movenode { display: inline-flex; align-items: center; gap: 8px; padding: 2px 8px; border: 1px solid transparent; border-radius: 6px; cursor: pointer; }
.movenode.disabled { color: #8a97a8; cursor: default; }
.movenode.picked { border-color: #bad2ed; background: #eaf3fd; color: #164f91; font-weight: 700; }
.movenode small { color: #8a97a8; font-size: 11px; }
.movetarget { margin-top: 10px; color: #526174; font-size: 13px; }
.hint { margin-top: 8px; color: #8a97a8; font-size: 12px; }
</style>
