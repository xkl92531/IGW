<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, toRaw, watch } from 'vue'
import { onBeforeRouteLeave, useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, type DatabaseStatusDto, type LicenseDto } from '../api'
import { projectApi } from '../features/project/api'
import { useTimedAutoSave } from '../composables/useTimedAutoSave'
import { applyProjectAutoSavePreferences, loadProjectAutoSaveSettings, projectAutoSaveSettings } from '../composables/projectAutoSaveSettings'
import { useI18n } from '../i18n'
import { licenseCustomerName, licenseStatusLabel, licenseStatusMessage } from '../utils/licensePresentation'
import { canEdit, session } from '../auth'

const { t } = useI18n()

const project = ref<any>(null)
const rev = ref('')
const health = ref<any>(null)
const license = ref<LicenseDto | null>(null)
/** 只有查看权限时整页只读：表单禁用、不显示保存。激活授权只有管理员能用。 */
const editable = computed(() => canEdit('settings'))
const isAdmin = computed(() => !!session.account?.isAdmin)
const opcUaLicensed = computed(() => license.value?.features?.find((item) => item.id === 'OpcUaServer')?.allowed !== false)
const licenseCode = ref('')
const licenseBusy = ref(false)
const route = useRoute()
const loading = ref(true)
const dirty = ref(false)
const editVersion = ref(0)
const autoSaveBlocked = ref(false)
const saveError = ref('')
const saving = ref(false)
const backgroundSaving = ref(false)
const saveBusy = computed(() => saving.value || backgroundSaving.value)

// 工程配置页的自动保存设置：存到网关(所有浏览器共用)，改动立即生效。
const autoSaveForm = ref({ enabled: projectAutoSaveSettings.enabled, seconds: projectAutoSaveSettings.seconds })
const autoSaveSettingBusy = ref(false)
void loadProjectAutoSaveSettings().then(() => {
  autoSaveForm.value = { enabled: projectAutoSaveSettings.enabled, seconds: projectAutoSaveSettings.seconds }
})
async function saveProjectAutoSaveSetting() {
  const seconds = Math.round(Number(autoSaveForm.value.seconds))
  if (!Number.isFinite(seconds) || seconds < 5 || seconds > 3600) {
    ElMessage.warning(t('自动保存间隔应在 5–3600 秒之间'))
    return
  }
  autoSaveSettingBusy.value = true
  try {
    applyProjectAutoSavePreferences(await api.updateProjectAutoSave(!!autoSaveForm.value.enabled, seconds))
    autoSaveForm.value = { enabled: projectAutoSaveSettings.enabled, seconds: projectAutoSaveSettings.seconds }
    ElMessage.success(t('自动保存设置已保存'))
  } catch (error: any) {
    autoSaveForm.value = { enabled: projectAutoSaveSettings.enabled, seconds: projectAutoSaveSettings.seconds }
    ElMessage.error(t('保存失败:') + (error?.response?.data?.error || error?.message || ''))
  } finally { autoSaveSettingBusy.value = false }
}

const form = ref({
  restPort: 48020,
  opcEnabled: true,
  opcPort: 4840,
  opcAnonymous: true,
  opcEncryption: true,
})

function hostForUrl() {
  const host = window.location.hostname || '127.0.0.1'
  return host.includes(':') && !host.startsWith('[') ? `[${host}]` : host
}
const restEndpoint = computed(() => `http://${hostForUrl()}:${form.value.restPort}`)
const opcEndpoint = computed(() => `opc.tcp://${hostForUrl()}:${form.value.opcPort}`)
const currentProjectName = computed(() => health.value?.project === 'Demo' ? t('项目') : (health.value?.project || '—'))

async function pollHealth() {
  try {
    health.value = await api.health()
    if (health.value?.license) license.value = health.value.license
  } catch { health.value = null }
}

const licenseLabel = computed(() => {
  return licenseStatusLabel(license.value, t)
})
const localizedLicenseCustomer = computed(() => licenseCustomerName(license.value?.customer, t))
const localizedLicenseMessage = computed(() => licenseStatusMessage(license.value, t))
const licenseColor = computed(() => {
  const status = license.value?.status
  if (status === 'Valid') return '#21863a'
  if (status === 'Trial' || status === 'ExpiringSoon' || status === 'Grace') return '#b36a00'
  return '#c0392b'
})

async function copyMachineId() {
  if (!license.value?.machineId) return
  try {
    await navigator.clipboard.writeText(license.value.machineId)
    ElMessage.success(t('机器码已复制'))
  } catch { ElMessage.warning(t('复制失败，请手动选择机器码复制')) }
}

async function activateLicense() {
  if (licenseBusy.value) return
  const code = licenseCode.value.trim()
  if (!code) { ElMessage.warning(t('请先粘贴授权码')); return }
  licenseBusy.value = true
  try {
    license.value = await api.activate(code)
    if (!license.value.blocked) {
      licenseCode.value = ''
      ElMessage.success(t('激活成功，采集已启用'))
    } else {
      ElMessage.error(t('激活未通过：') + licenseLabel.value)
    }
    await pollHealth()
  } catch (e: any) {
    ElMessage.error(t('激活失败：') + (e?.response?.data?.error || e?.message || t('未知错误')))
  } finally { licenseBusy.value = false }
}

function focusSection() {
  if (route.query.focus !== 'license') return
  nextTick(() => document.getElementById('license-settings')?.scrollIntoView({ behavior: 'smooth', block: 'start' }))
}
watch(() => route.query.focus, focusSection)

async function load() {
  loading.value = true
  try {
  const p = await projectApi.settings()
  project.value = p
  rev.value = projectApi.revision()
  form.value = {
    restPort: p.settings?.restPort ?? 48020,
    opcEnabled: p.opcUa?.enabled !== false,
    opcPort: p.opcUa?.port ?? p.settings?.opcUaPort ?? 4840,
    opcAnonymous: p.opcUa?.allowAnonymous !== false,
    opcEncryption: p.opcUa?.allowEncryption !== false,
  }
  await nextTick()
  dirty.value = false
  autoSaveBlocked.value = false
  saveError.value = ''
  } finally { loading.value = false }
}

watch(form, () => {
  if (loading.value) return
  dirty.value = true
  editVersion.value++
}, { deep: true })

function validate(value = form.value): string {
  const ports = [
    [t('REST 端口'), Number(value.restPort)],
    [t('OPC UA 端口'), Number(value.opcPort)],
  ] as const
  for (const [name, port] of ports)
    if (!Number.isInteger(port) || port < 1 || port > 65535) return t('{name}必须是 1-65535 的整数', { name })
  if (value.opcEnabled && value.restPort === value.opcPort) return t('REST 端口和 OPC UA 端口不能相同')
  return ''
}

function writeSettings(target: any, value: typeof form.value) {
  target.settings ||= {}
  target.opcUa ||= {}
  target.settings.restPort = Number(value.restPort)
  target.settings.opcUaPort = Number(value.opcPort) // 兼容桌面托盘版读取
  target.opcUa.enabled = !!value.opcEnabled
  target.opcUa.port = Number(value.opcPort)
  target.opcUa.allowAnonymous = !!value.opcAnonymous
  target.opcUa.allowEncryption = !!value.opcEncryption
}

async function save(automatic = false): Promise<boolean> {
  if (!project.value || !dirty.value) {
    if (!automatic) ElMessage.info(t('设置没有变化'))
    return true
  }
  if (saveBusy.value) return false
  const formSnapshot = structuredClone(toRaw(form.value))
  const invalid = validate(formSnapshot)
  if (invalid) {
    saveError.value = invalid
    if (!automatic) ElMessage.warning(invalid)
    return false
  }
  const savedVersion = editVersion.value
  const projectSnapshot = structuredClone(toRaw(project.value))
  writeSettings(projectSnapshot, formSnapshot)
  const activeSaving = automatic ? backgroundSaving : saving
  activeSaving.value = true
  saveError.value = ''
  try {
    await projectApi.mutate({
      kind: 'replaceProjectSections', entityKind: 'project', entityIds: ['settings', 'opcUa'],
      targetId: null, documentJson: JSON.stringify({ settings: projectSnapshot.settings, opcUa: projectSnapshot.opcUa }),
    }, rev.value)
    const current = await projectApi.settings()
    project.value = current
    rev.value = projectApi.revision()
    if (editVersion.value === savedVersion) dirty.value = false
    autoSaveBlocked.value = false
    if (!automatic) ElMessage.success(t('IGW 设置已保存并应用'))
    return true
  } catch (e: any) {
    saveError.value = e?.response?.data?.error || e?.message || t('未知错误')
    if (e?.response?.status === 409) {
      autoSaveBlocked.value = true
      if (!automatic)
        await ElMessageBox.confirm(saveError.value + '\n' + t('重新载入并放弃本页未保存修改？'), t('保存冲突'),
          { type: 'warning', confirmButtonText: t('重新载入'), cancelButtonText: t('先不') }).then(load).catch(() => {})
    } else if (!automatic) ElMessage.error(t('保存失败：') + saveError.value)
    return false
  } finally {
    activeSaving.value = false
  }
}

const autoSave = useTimedAutoSave({
  dirty,
  saving: saveBusy,
  changeVersion: editVersion,
  save: () => save(true),
  canSave: () => !autoSaveBlocked.value,
  intervalMs: 30_000,
})
const autoSaveState = autoSave.state
const autoSavedText = autoSave.lastSavedText

async function reload() {
  if (loading.value || saveBusy.value) return
  if (dirty.value) {
    try { await ElMessageBox.confirm(t('重新载入会放弃本页未保存的设置，继续？'), t('重新载入'), { type: 'warning' }) }
    catch { return }
  }
  try { await load(); ElMessage.success(t('IGW 设置已重新载入')) }
  catch (e: any) { ElMessage.error(t('读取 IGW 设置失败：') + (e?.message || '')) }
}

onBeforeRouteLeave(async () => {
  if (dirty.value && !(await autoSave.flush())) {
    ElMessage.error(t('IGW 设置尚未保存，已取消离开页面'))
    return false
  }
  return true
})

// 数据存储(安装时选择)：外部主库时显示同步状态；主库断开时网关用本机缓存运行并暂停修改配置。
const database = ref<DatabaseStatusDto | null>(null)
const databaseStateLabel = computed(() => ({
  sqlite: t('本机内置数据库'), online: t('已同步'), syncing: t('同步中'), offline: t('主数据库未连接，使用本机缓存运行'),
} as Record<string, string>)[database.value?.state || ''] || database.value?.state || '')
const databaseStateTone = computed(() => ({ sqlite: 'ok', online: 'ok', syncing: 'warn', offline: 'bad' } as Record<string, string>)[database.value?.state || ''] || 'warn')
async function pollDatabase() {
  try { database.value = await api.databaseStatus() } catch { /* 旧版网关没有该接口时不显示 */ }
}

let healthTimer: ReturnType<typeof setInterval> | undefined
onMounted(async () => {
  try { await load() } catch (e: any) { ElMessage.error(t('读取 IGW 设置失败：') + (e?.message || '')) }
  await Promise.all([pollHealth(), pollDatabase()])
  focusSection()
  healthTimer = setInterval(() => { void pollHealth(); void pollDatabase() }, 3000)
})
onUnmounted(() => { if (healthTimer) clearInterval(healthTimer) })
</script>

<template>
  <div class="settings-page" v-loading="loading">
    <div class="page-head">
      <div>
        <div class="eyebrow">IGW GATEWAY</div>
        <h2>{{ $t('IGW 设置') }}</h2>
        <p>{{ $t('配置网关自身的服务地址和 OPC UA 服务，不包含 PLC 工程结构。') }}</p>
      </div>
      <div class="actions">
        <el-button :disabled="saveBusy" @click="reload">{{ $t('重新载入') }}</el-button>
        <el-button v-if="editable" type="primary" :loading="saving" :disabled="saveBusy" @click="save(false)">{{ $t('保存并应用') }}</el-button>
      </div>
    </div>

    <div class="save-state">
      <el-tag v-if="saving" type="info" effect="light">{{ $t('保存中…') }}</el-tag>
      <el-tag v-else-if="autoSaveBlocked" type="danger" effect="light" :title="saveError">{{ $t('自动保存冲突，请手动处理') }}</el-tag>
      <el-tag v-else-if="dirty" :type="autoSaveState === 'error' ? 'danger' : 'warning'" effect="light"
              :title="saveError || $t('有改动时每 30 秒在后台静默保存一次，不阻塞操作')">
        {{ $t(autoSaveState === 'error' ? '自动保存失败，等待重试' : '待后台保存（每 30 秒）') }}
      </el-tag>
      <el-tag v-else type="success" effect="plain">
        {{ autoSavedText ? $t('已自动保存 {time}', { time: autoSavedText }) : $t('设置已同步 · 自动保存已开启') }}
      </el-tag>
    </div>

    <div class="settings-grid">
      <section class="setting-card">
        <div class="card-title"><span class="card-icon service">◎</span><div><b>{{ $t('服务端口') }}</b><small>{{ $t('网页配置和北向 REST API') }}</small></div></div>
        <el-form label-width="128px" label-position="left" :disabled="!editable">
          <el-form-item :label="$t('REST 端口')">
            <el-input-number v-model="form.restPort" :min="1" :max="65535" controls-position="right" />
          </el-form-item>
          <el-form-item :label="$t('REST / 网页地址')">
            <el-input :model-value="restEndpoint" readonly><template #append>{{ $t('当前主机') }}</template></el-input>
          </el-form-item>
        </el-form>
        <div class="notice warn">{{ $t('REST 端口保存后需要重启 IGW Runtime 才能切换监听地址；重启后请用新端口重新打开本页面。') }}</div>
      </section>

      <section class="setting-card">
        <div class="card-title"><span class="card-icon opc">UA</span><div><b>{{ $t('OPC UA 服务器') }}</b><small>{{ $t('DCS、SCADA 和 UAExpert 连接本网关') }}</small></div></div>
        <el-form label-width="128px" label-position="left" :disabled="!editable || !opcUaLicensed">
          <el-form-item :label="$t('启用服务')"><el-switch v-model="form.opcEnabled" /></el-form-item>
          <el-form-item :label="$t('OPC UA 端口')">
            <el-input-number v-model="form.opcPort" :min="1" :max="65535" controls-position="right" :disabled="!form.opcEnabled" />
          </el-form-item>
          <el-form-item :label="$t('OPC UA 端点')"><el-input :model-value="opcEndpoint" readonly /></el-form-item>
          <el-form-item :label="$t('允许匿名访问')"><el-switch v-model="form.opcAnonymous" :disabled="!form.opcEnabled" /></el-form-item>
          <el-form-item :label="$t('允许加密')"><el-switch v-model="form.opcEncryption" :disabled="!form.opcEnabled" /></el-form-item>
        </el-form>
        <div v-if="!opcUaLicensed" class="notice warn">{{ $t('授权码不包含 OPC UA 服务，服务不会启动。需要请联系供应商更换授权码。') }}</div>
        <div v-else class="notice ok">{{ $t('OPC UA 设置保存后会自动重建服务，现有 OPC UA 客户端会短暂重连。') }}</div>
      </section>

      <section class="setting-card" data-test="project-autosave-settings">
        <div class="card-title"><span class="card-icon service">⟳</span><div><b>{{ $t('工程配置自动保存') }}</b><small>{{ $t('工程配置页有修改时的后台保存方式') }}</small></div></div>
        <el-form label-width="128px" label-position="left" :disabled="!editable">
          <el-form-item :label="$t('自动保存')">
            <el-switch v-model="autoSaveForm.enabled" :loading="autoSaveSettingBusy" @change="saveProjectAutoSaveSetting" />
          </el-form-item>
          <el-form-item :label="$t('保存间隔（秒）')">
            <el-input-number v-model="autoSaveForm.seconds" :min="5" :max="3600" :step="5" controls-position="right"
                             :disabled="!autoSaveForm.enabled || autoSaveSettingBusy" @change="saveProjectAutoSaveSetting" />
          </el-form-item>
        </el-form>
        <div class="notice ok">{{ $t('属性对话框点“确定”会立即保存；自动保存只处理其他改动（剪切、粘贴、拖动等）。关闭后这些改动需要点击“保存并应用”。') }}</div>
      </section>

      <section v-if="database" class="setting-card" data-test="database-settings">
        <div class="card-title"><span class="card-icon service">DB</span><div><b>{{ $t('数据存储') }}</b><small>{{ $t('工程配置、采集流程、IGW 设置和运行数据的存储位置') }}</small></div></div>
        <div class="runtime-grid">
          <div><span>{{ $t('数据库类型') }}</span><b>{{ database.databaseKind }}</b></div>
          <div><span>{{ $t('连接') }}</span><b>{{ database.target }}</b></div>
          <div><span>{{ $t('状态') }}</span><b :class="databaseStateTone === 'ok' ? 'online' : databaseStateTone === 'bad' ? 'offline' : ''">{{ databaseStateLabel }}</b></div>
          <div v-if="database.state !== 'sqlite'"><span>{{ $t('待同步') }}</span><b>{{ database.pending }}</b></div>
          <div v-if="database.lastSyncUtc"><span>{{ $t('最近同步') }}</span><b>{{ new Date(database.lastSyncUtc).toLocaleString() }}</b></div>
        </div>
        <div v-if="database.state === 'offline'" class="notice warn">{{ database.message || $t('主数据库暂时无法连接。') }} {{ $t('采集和上报照常运行，修改的数据先保存在本机，连接恢复后自动补传；在此期间暂停修改配置。') }}</div>
        <div v-else-if="database.state !== 'sqlite'" class="notice ok">{{ $t('本机保留一份完整的数据副本：主数据库断开时网关照常运行，恢复后自动补传。更换数据库请重新运行安装程序。') }}</div>
      </section>

      <section class="setting-card runtime-card">
        <div class="card-title"><span class="card-icon runtime">●</span><div><b>{{ $t('当前运行状态') }}</b><small>{{ $t('每 3 秒刷新一次') }}</small></div></div>
        <div class="runtime-grid" v-if="health">
          <div><span>{{ $t('网关版本') }}</span><b data-test="gateway-version">{{ health.version || $t('未知') }}</b></div>
          <div><span>{{ $t('运行状态') }}</span><b class="online">{{ health.status === 'ok' ? $t('正常') : health.status }}</b></div>
          <div><span>{{ $t('当前工程') }}</span><b>{{ currentProjectName }}</b></div>
          <div><span>{{ $t('设备在线') }}</span><b>{{ health.devicesOnline }} / {{ health.devices }}</b></div>
          <div><span>{{ $t('活动标记') }}</span><b>{{ health.activeTags }} / {{ health.tags }}</b></div>
          <div><span>{{ $t('OPC UA 会话') }}</span><b>{{ health.opcUaSessions }}</b></div>
          <div><span>{{ $t('网页连接') }}</span><b class="online">{{ $t('已连接') }}</b></div>
        </div>
        <el-empty v-else :description="$t('IGW Runtime 未连接')" :image-size="62" />
      </section>

      <section id="license-settings" class="setting-card license-card">
        <div class="card-title"><span class="card-icon license">◆</span><div><b>{{ $t('授权与激活') }}</b><small>{{ $t('本机授权状态、机器码和许可证激活') }}</small></div></div>
        <div class="license-layout">
          <div class="license-status">
            <span class="license-dot" :style="{ background: licenseColor }"></span>
            <div>
              <b :style="{ color: licenseColor }">{{ licenseLabel }}</b>
              <p v-if="localizedLicenseCustomer">{{ $t('客户：') }}{{ localizedLicenseCustomer }}</p>
              <p v-if="license?.expiry">{{ $t('到期：') }}{{ license.expiry }}<template v-if="license.daysLeft >= 0"> · {{ $t('剩 {days} 天', { days: license.daysLeft }) }}</template></p>
              <small>{{ localizedLicenseMessage }}</small>
            </div>
          </div>
          <div class="machine-box">
            <label>{{ $t('本机机器码') }}</label>
            <div><code>{{ license?.machineId || '—' }}</code><el-button @click="copyMachineId">{{ $t('复制') }}</el-button></div>
            <small>{{ $t('将机器码发给授权提供方获取本机授权码。') }}</small>
          </div>
          <div v-if="isAdmin" class="activate-box">
            <label>{{ $t('授权码') }}</label>
            <el-input v-model="licenseCode" type="textarea" :rows="3" :placeholder="$t('粘贴 IGW1... 授权码')" />
            <el-button type="primary" :loading="licenseBusy" @click="activateLicense">{{ $t('激活授权') }}</el-button>
          </div>
          <div v-else class="activate-box">
            <label>{{ $t('授权码') }}</label>
            <small class="admin-only">{{ $t('只有管理员可以激活或更换授权码。') }}</small>
          </div>
        </div>
        <div v-if="license?.drivers?.length" class="entitlements" data-test="license-entitlements">
          <div>
            <label>{{ $t('授权驱动') }}</label>
            <span v-if="!license.driversLimited" class="ent-all">{{ $t('全部驱动') }}</span>
            <template v-else>
              <el-tag v-for="item in license.drivers" :key="item.id" size="small" :type="item.allowed ? 'success' : 'info'"
                :effect="item.allowed ? 'light' : 'plain'" :class="{ 'ent-off': !item.allowed }">{{ $t(item.name) }}</el-tag>
            </template>
          </div>
          <div>
            <label>{{ $t('授权功能') }}</label>
            <span v-if="!license.featuresLimited" class="ent-all">{{ $t('全部功能') }}</span>
            <template v-else>
              <el-tag v-for="item in license.features" :key="item.id" size="small" :type="item.allowed ? 'success' : 'info'"
                :effect="item.allowed ? 'light' : 'plain'" :class="{ 'ent-off': !item.allowed }">{{ $t(item.name) }}</el-tag>
            </template>
          </div>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.settings-page { max-width: 1120px; margin: 0 auto; }
.page-head { display: flex; justify-content: space-between; align-items: flex-end; gap: 20px; margin-bottom: 8px; }
.eyebrow { color: #2e78d6; font-size: 11px; font-weight: 700; letter-spacing: 1.4px; }
h2 { margin: 4px 0; color: #213247; font-size: 24px; }
.page-head p { margin: 0; color: #7d8997; font-size: 13px; }
.actions { display: flex; gap: 8px; }
.save-state { display: flex; justify-content: flex-end; min-height: 28px; margin-bottom: 8px; }
.settings-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
.setting-card { background: #fff; border: 1px solid #e2e8f0; border-radius: 10px; padding: 18px 20px; box-shadow: 0 3px 12px rgba(29, 49, 73, .05); }
.card-title { display: flex; align-items: center; gap: 11px; margin-bottom: 18px; }
.card-title > div { display: flex; flex-direction: column; gap: 3px; }
.card-title b { color: #26384e; font-size: 16px; }
.card-title small { color: #8a96a3; }
.card-icon { width: 34px; height: 34px; border-radius: 9px; display: inline-flex; align-items: center; justify-content: center; font-weight: 800; }
.card-icon.service { color: #1769aa; background: #e7f2fb; font-size: 22px; }
.card-icon.opc { color: #6c3eb1; background: #f1eafb; font-size: 12px; }
.card-icon.runtime { color: #21863a; background: #e7f5ea; font-size: 13px; }
.card-icon.license { color: #a56b00; background: #fff3d4; font-size: 13px; }
:deep(.el-input-number) { width: 100%; }
.notice { border-radius: 6px; padding: 8px 10px; font-size: 12px; line-height: 1.55; margin-top: 8px; }
.notice.warn { color: #956008; background: #fff7e1; border: 1px solid #f2dfaa; }
.notice.ok { color: #28723a; background: #edf8f0; border: 1px solid #cde8d3; }
.runtime-card { grid-column: 1 / -1; }
.license-card { grid-column: 1 / -1; scroll-margin-top: 14px; }
.runtime-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; }
.runtime-grid > div { display: flex; flex-direction: column; gap: 5px; background: #f6f8fb; border: 1px solid #edf0f4; border-radius: 7px; padding: 11px 13px; }
.runtime-grid span { color: #8994a1; font-size: 12px; }
.runtime-grid b { color: #34465b; font-size: 14px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.runtime-grid .online { color: #21863a; }
.license-layout { display: grid; grid-template-columns: .85fr 1.15fr 1.15fr; gap: 12px; align-items: stretch; }
.license-status, .machine-box, .activate-box { border: 1px solid #e7ebf0; border-radius: 8px; padding: 13px 14px; background: #f8fafc; }
.license-status { display: flex; gap: 10px; align-items: flex-start; }
.license-dot { width: 11px; height: 11px; border-radius: 50%; margin-top: 5px; flex-shrink: 0; }
.license-status b { font-size: 15px; }
.license-status p { color: #566577; font-size: 12px; margin: 6px 0 0; }
.license-status small, .machine-box small { display: block; color: #8a96a3; font-size: 11px; line-height: 1.5; margin-top: 7px; }
.machine-box label, .activate-box label { display: block; color: #536274; font-size: 12px; font-weight: 600; margin-bottom: 8px; }
.machine-box > div { display: flex; gap: 8px; align-items: center; }
.machine-box code { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #1a5fb4; background: #edf4ff; border: 1px solid #c9daf2; border-radius: 5px; padding: 8px 9px; font-size: 14px; font-weight: 700; }
.activate-box :deep(textarea) { font-family: Consolas, monospace; }
.activate-box .el-button { margin-top: 8px; }
.admin-only { color: #8a96a3; font-size: 12px; }
.entitlements { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; margin-top: 12px; }
.entitlements > div { border: 1px solid #e7ebf0; border-radius: 8px; padding: 11px 14px; background: #f8fafc; display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
.entitlements label { color: #536274; font-size: 12px; font-weight: 600; margin-right: 6px; }
.entitlements .ent-off { text-decoration: line-through; color: #a0a8b3; }
.ent-all { color: #21863a; font-size: 13px; }
@media (max-width: 850px) {
  .settings-grid { grid-template-columns: 1fr; }
  .runtime-card { grid-column: auto; }
  .runtime-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .license-card { grid-column: auto; }
  .license-layout { grid-template-columns: 1fr; }
  .entitlements { grid-template-columns: 1fr; }
  .page-head { align-items: flex-start; }
}
</style>
