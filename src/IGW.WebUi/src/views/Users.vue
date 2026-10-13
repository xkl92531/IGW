<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { errorMessage, usersApi, type AccessLevel, type AccountDto, type ModuleDto } from '../auth'
import { useI18n } from '../i18n'

/** 用户与权限页面(只有管理员能进)：新建、改权限、重置密码、停用、删除。管理员账号只有一个，不能删除。 */
const { t } = useI18n()
const users = ref<AccountDto[]>([])
const modules = ref<ModuleDto[]>([])
const loading = ref(false)
const saving = ref(false)
const dialog = ref(false)
const editing = ref<AccountDto | null>(null)
const form = reactive({
  username: '',
  displayName: '',
  password: '',
  permissions: {} as Record<string, AccessLevel>,
})
const levels: { value: AccessLevel; label: string }[] = [
  { value: 'None', label: '不可见' },
  { value: 'View', label: '只能看' },
  { value: 'Edit', label: '可修改' },
]
const dialogTitle = computed(() => editing.value ? t('编辑用户 {name}', { name: editing.value.username }) : t('新建用户'))

async function load() {
  loading.value = true
  try {
    const result = await usersApi.list()
    users.value = result.users
    modules.value = result.modules
  } catch (error) { ElMessage.error(errorMessage(error, t('读取用户列表失败'))) }
  finally { loading.value = false }
}
onMounted(load)

function levelLabel(level: AccessLevel) {
  return t(levels.find((item) => item.value === level)?.label || '不可见')
}

function openCreate() {
  editing.value = null
  form.username = ''; form.displayName = ''; form.password = ''
  form.permissions = Object.fromEntries(modules.value.map((module) => [module.id, 'None' as AccessLevel]))
  dialog.value = true
}

function openEdit(user: AccountDto) {
  editing.value = user
  form.username = user.username; form.displayName = user.displayName; form.password = ''
  form.permissions = { ...user.permissions }
  dialog.value = true
}

function setAll(level: AccessLevel) {
  for (const module of modules.value) form.permissions[module.id] = level
}

async function submit() {
  if (saving.value) return
  saving.value = true
  try {
    if (editing.value) {
      await usersApi.update(editing.value.username, {
        displayName: form.displayName,
        permissions: form.permissions,
        password: form.password || undefined,
      })
      ElMessage.success(form.password ? t('已保存，用户下次登录需要修改密码') : t('已保存'))
    } else {
      await usersApi.create({ username: form.username, displayName: form.displayName, password: form.password, permissions: form.permissions })
      ElMessage.success(t('已创建用户，首次登录需要修改密码'))
    }
    dialog.value = false
    await load()
  } catch (error) { ElMessage.error(errorMessage(error, t('保存失败'))) }
  finally { saving.value = false }
}

async function toggleDisabled(user: AccountDto) {
  try {
    await usersApi.update(user.username, { disabled: !user.disabled })
    ElMessage.success(user.disabled ? t('已启用') : t('已停用，该用户会立即退出登录'))
    await load()
  } catch (error) { ElMessage.error(errorMessage(error, t('操作失败'))) }
}

async function remove(user: AccountDto) {
  try {
    await ElMessageBox.confirm(t('确定删除用户 {name}？删除后不能恢复。', { name: user.username }), t('删除用户'),
      { type: 'warning', confirmButtonText: t('删除'), cancelButtonText: t('取消') })
  } catch { return }
  try {
    await usersApi.remove(user.username)
    ElMessage.success(t('已删除'))
    await load()
  } catch (error) { ElMessage.error(errorMessage(error, t('删除失败'))) }
}

function lastLogin(user: AccountDto) {
  return user.lastLoginUtc ? new Date(user.lastLoginUtc).toLocaleString() : t('从未登录')
}
</script>

<template>
  <div class="users-page" data-test="user-manager">
    <div class="page-head">
      <div>
        <div class="eyebrow">IGW GATEWAY</div>
        <h2>{{ t('用户与权限') }}</h2>
        <p>{{ t('管理员可以新建用户，并给每个页面分配「不可见 / 只能看 / 可修改」') }}</p>
      </div>
      <div class="actions">
        <el-button :disabled="loading" @click="load">{{ t('刷新') }}</el-button>
        <el-button type="primary" @click="openCreate">{{ t('+ 新建用户') }}</el-button>
      </div>
    </div>
    <section class="users-card">
      <el-table v-loading="loading" :data="users" size="small" border>
        <el-table-column :label="t('用户名')" min-width="130">
          <template #default="{ row }">
            <b>{{ row.username }}</b>
            <el-tag v-if="row.isAdmin" size="small" class="role-tag">{{ t('管理员') }}</el-tag>
            <el-tag v-if="row.disabled" size="small" type="info" class="role-tag">{{ t('已停用') }}</el-tag>
            <div v-if="row.displayName" class="sub">{{ row.displayName }}</div>
          </template>
        </el-table-column>
        <el-table-column v-for="module in modules" :key="module.id" :label="t(module.name)" min-width="86" align="center">
          <template #default="{ row }">
            <span :class="['level', 'level-' + row.permissions[module.id]]">{{ levelLabel(row.permissions[module.id]) }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="t('最近登录')" min-width="150">
          <template #default="{ row }"><span class="sub">{{ lastLogin(row) }}</span></template>
        </el-table-column>
        <el-table-column :label="t('操作')" width="190" fixed="right">
          <template #default="{ row }">
            <template v-if="!row.isAdmin">
              <el-button link type="primary" @click="openEdit(row)">{{ t('编辑') }}</el-button>
              <el-button link @click="toggleDisabled(row)">{{ row.disabled ? t('启用') : t('停用') }}</el-button>
              <el-button link type="danger" @click="remove(row)">{{ t('删除') }}</el-button>
            </template>
            <span v-else class="sub">{{ t('右上角账号菜单修改密码') }}</span>
          </template>
        </el-table-column>
      </el-table>
    </section>

    <el-dialog v-model="dialog" :title="dialogTitle" width="560px" append-to-body>
      <el-form label-width="96px" label-position="left">
        <el-form-item :label="t('用户名')">
          <el-input v-model="form.username" :disabled="!!editing" :placeholder="t('字母、数字、下划线，2–32 位')" autocomplete="off" />
        </el-form-item>
        <el-form-item :label="t('显示名')">
          <el-input v-model="form.displayName" :placeholder="t('选填，如 张三 / 一号线操作员')" />
        </el-form-item>
        <el-form-item :label="editing ? t('重置密码') : t('初始密码')">
          <el-input v-model="form.password" type="password" show-password autocomplete="new-password"
            :placeholder="editing ? t('不改密码请留空') : t('至少 6 位，用户首次登录要自己改')" />
        </el-form-item>
        <el-form-item :label="t('页面权限')">
          <div class="perm-table">
            <div class="perm-quick">
              <span>{{ t('全部设为') }}</span>
              <el-button v-for="level in levels" :key="level.value" size="small" @click="setAll(level.value)">{{ t(level.label) }}</el-button>
            </div>
            <div v-for="module in modules" :key="module.id" class="perm-row">
              <span class="perm-name">{{ t(module.name) }}</span>
              <el-radio-group v-model="form.permissions[module.id]" size="small">
                <el-radio-button v-for="level in levels" :key="level.value" :value="level.value">{{ t(level.label) }}</el-radio-button>
              </el-radio-group>
            </div>
          </div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">{{ t('取消') }}</el-button>
        <el-button type="primary" :loading="saving" @click="submit">{{ t('保存') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.users-page { max-width: 1120px; margin: 0 auto; }
.page-head { display: flex; justify-content: space-between; align-items: flex-end; gap: 20px; margin-bottom: 16px; }
.eyebrow { color: #2e78d6; font-size: 11px; font-weight: 700; letter-spacing: 1.4px; }
h2 { margin: 4px 0; color: #213247; font-size: 24px; }
.page-head p { margin: 0; color: #7d8997; font-size: 13px; }
.actions { display: flex; gap: 8px; }
.users-card { background: #fff; border: 1px solid #e2e8f0; border-radius: 10px; padding: 18px 20px; box-shadow: 0 3px 12px rgba(29, 49, 73, .05); }
.role-tag { margin-left: 6px; }
.sub { color: #8a96a3; font-size: 12px; }
.level { font-size: 12px; border-radius: 4px; padding: 1px 6px; }
.level-None { color: #98a2b3; }
.level-View { color: #1769aa; background: #e7f2fb; }
.level-Edit { color: #21863a; background: #e7f5ea; }
.perm-table { display: flex; flex-direction: column; gap: 8px; width: 100%; }
.perm-quick { display: flex; align-items: center; gap: 6px; color: #8a96a3; font-size: 12px; }
.perm-quick .el-button { margin-left: 0; }
.perm-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.perm-name { color: #34465b; }
</style>
