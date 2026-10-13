# IGW Web UI Bilingual Language Switch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add immediate, automatically persisted Chinese/English switching to every formal IGW Web UI route and business dialog without changing project or PLC data.

**Architecture:** A dependency-free reactive language service owns locale normalization, interpolation, and fallback. A gateway-side SQLite preference store is authoritative across hard refreshes and Runtime restarts; browser storage is only an offline fallback. Chinese source copy is used as the stable message key so existing Chinese UI tests remain meaningful; the English catalog maps those keys to operator-facing English. `App.vue` owns the single language menu and dynamically supplies the matching Element Plus locale.

**Tech Stack:** Vue 3.5, TypeScript 5.7, Element Plus 2.9, Node test runner, Vite 6.

## Global Constraints

- The only language entry is the top-bar menu; changing it immediately persists with no Apply or Save action.
- Default locale is `zh-CN`; supported locales are exactly `zh-CN` and `en-US`.
- Do not translate PLC/channel/device/DB/tag names, PLC comments, user titles, project data, MES payloads, or raw third-party errors.
- Switching language must not reload the route, project, runtime state, or MES canvas.
- Missing English copy falls back to the Chinese source key.
- Do not add a runtime internationalization dependency.

---

### Task 1: Reactive locale service and catalog contract

**Files:**
- Create: `src/IGW.WebUi/src/i18n/catalog.ts`
- Create: `src/IGW.WebUi/src/i18n/runtime.ts`
- Create: `src/IGW.WebUi/src/i18n/index.ts`
- Create: `src/IGW.WebUi/src/i18n/runtime.test.cjs`

**Interfaces:**
- Produces: `type IgwLocale = 'zh-CN' | 'en-US'`
- Produces: `locale: Ref<IgwLocale>`, `setLocale(next: IgwLocale): void`, `t(source: string, params?: Record<string, string | number>): string`, `installI18n(app: App): void`
- Uses browser key `igw.web.locale` only as a startup fallback; the authoritative value is stored in `%ProgramData%\IGW\ui-preferences.db` through `/api/v1/ui/preferences`.

- [ ] **Step 1: Write the failing runtime test**

```js
test('locale defaults to Chinese, switches immediately, persists, interpolates and falls back', () => {
  const storage = memoryStorage()
  const runtime = createI18nRuntime(storage, { '工程配置': 'Project', '已选 {count} 个': '{count} selected' })
  assert.equal(runtime.locale.value, 'zh-CN')
  runtime.setLocale('en-US')
  assert.equal(storage.getItem('igw.web.locale'), 'en-US')
  assert.equal(runtime.t('已选 {count} 个', { count: 2 }), '2 selected')
  assert.equal(runtime.t('PLC1'), 'PLC1')
})
```

- [ ] **Step 2: Run the test and verify RED**

Run: `node --test src/i18n/runtime.test.cjs` from `src/IGW.WebUi`.

Expected: FAIL because `runtime.ts` and its exports do not exist.

- [ ] **Step 3: Implement the minimal runtime**

```ts
export const STORAGE_KEY = 'igw.web.locale'
export type IgwLocale = 'zh-CN' | 'en-US'

export function normalizeLocale(value: unknown): IgwLocale {
  return value === 'en-US' ? 'en-US' : 'zh-CN'
}

export function interpolate(text: string, params: Record<string, string | number> = {}) {
  return text.replace(/\{(\w+)\}/g, (_, key) => String(params[key] ?? `{${key}}`))
}
```

The runtime reads storage once, changes the shared Vue ref synchronously, writes storage inside `setLocale`, updates `document.documentElement.lang` when a document exists, and translates only known catalog keys.

- [ ] **Step 4: Run runtime tests and verify GREEN**

Run: `node --test src/i18n/runtime.test.cjs`.

Expected: all locale runtime tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.WebUi/src/i18n
git commit -m "feat: add persistent Web UI locale service"
```

### Task 2: Top-bar switch and Element Plus locale

**Files:**
- Modify: `src/IGW.WebUi/src/main.ts`
- Modify: `src/IGW.WebUi/src/App.vue`
- Create: `src/IGW.WebUi/src/App.i18n.test.cjs`

**Interfaces:**
- Consumes: `installI18n`, `locale`, `setLocale`, and `t` from Task 1.
- Produces: one top-bar dropdown labeled `中文` or `EN` and one dynamic `el-config-provider` locale.

- [ ] **Step 1: Write the failing shell test**

Assert that `App.vue` contains a dropdown command handler, `setLocale`, `中文`, `English`, a dynamic Element Plus config provider, and no language Apply/Save button.

- [ ] **Step 2: Run the shell test and verify RED**

Run: `node --test src/App.i18n.test.cjs`.

Expected: FAIL because the language menu is absent.

- [ ] **Step 3: Implement the shell integration**

Register the i18n plugin before mounting. Replace the static Element Plus locale registration with root `<el-config-provider :locale="elementLocale">`. Add a compact `el-dropdown` before connection status:

```vue
<el-dropdown trigger="click" @command="setLocale">
  <button class="language-switch" type="button" :aria-label="t('切换语言')">
    <el-icon><Location /></el-icon><span>{{ locale === 'zh-CN' ? '中文' : 'EN' }}</span>
  </button>
  <template #dropdown>
    <el-dropdown-menu>
      <el-dropdown-item command="zh-CN">中文</el-dropdown-item>
      <el-dropdown-item command="en-US">English</el-dropdown-item>
    </el-dropdown-menu>
  </template>
</el-dropdown>
```

Translate brand subtitle, navigation, connection text, and license states with `t()`.

- [ ] **Step 4: Run shell and runtime tests**

Run: `node --test src/i18n/runtime.test.cjs src/App.i18n.test.cjs`.

Expected: both files pass.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.WebUi/src/main.ts src/IGW.WebUi/src/App.vue src/IGW.WebUi/src/App.i18n.test.cjs src/IGW.WebUi/src/i18n/catalog.ts
git commit -m "feat: add top-bar language switch"
```

### Task 3: Engineering configuration route

**Files:**
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/components/CsvImportDialog.vue`
- Modify: `src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.vue`
- Modify: `src/IGW.WebUi/src/components/SiemensAuthenticationDialog.vue`
- Modify: `src/IGW.WebUi/src/components/ValidationReport.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Create: `src/IGW.WebUi/src/views/Project.i18n.test.cjs`

**Interfaces:**
- Consumes: global `$t()` in templates and `useI18n().t` in scripts.
- Produces: bilingual project toolbar, trees, tables, context menus, property editors, reconnect/diagnostic flows, import dialogs and confirmation messages.

- [ ] **Step 1: Write a failing source contract**

The test must assert representative copy is routed through translation calls: `工程配置`, `重新加载工程`, `重连`, `诊断`, `自动生成标记`, `属性`, `删除`, `确定`, `取消`, and the OPC UA browse headings.

- [ ] **Step 2: Run the Project i18n test and verify RED**

Run: `node --test src/views/Project.i18n.test.cjs`.

- [ ] **Step 3: Migrate user-facing fixed copy**

Use Chinese source copy as the key: `{{ $t('工程配置') }}`, `:title="$t('重新加载工程')"`, and `ElMessage.success(t('工程已保存'))`. Do not wrap `node.name`, tag descriptions, endpoint URLs, addresses, imported comments, or API error details.

- [ ] **Step 4: Run Project tests**

Run: `node --test src/views/Project*.test.cjs src/components/OpcUaTagBrowserDialog.ui.test.cjs src/components/SiemensAuthenticationDialog.test.cjs`.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.WebUi/src/views/Project.vue src/IGW.WebUi/src/views/Project.i18n.test.cjs src/IGW.WebUi/src/components src/IGW.WebUi/src/i18n/catalog.ts
git commit -m "feat: translate engineering configuration UI"
```

### Task 4: Monitor and MES flow route

**Files:**
- Modify: `src/IGW.WebUi/src/views/Monitor.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Modify: `src/IGW.WebUi/src/components/ApplyTemplateDialog.vue`
- Modify: `src/IGW.WebUi/src/components/FlowTemplatePanel.vue`
- Modify: `src/IGW.WebUi/src/components/MesDataRangeDialog.vue`
- Modify: `src/IGW.WebUi/src/components/MesNodeDetail.vue`
- Modify: `src/IGW.WebUi/src/components/MesParameterPicker.vue`
- Modify: `src/IGW.WebUi/src/components/MesResponseFieldPicker.vue`
- Modify: `src/IGW.WebUi/src/components/MesTimeline.vue`
- Modify: `src/IGW.WebUi/src/components/TemplateDiffPanel.vue`
- Modify: `src/IGW.WebUi/src/components/TemplateEditorDialog.vue`
- Modify: `src/IGW.WebUi/src/components/TemplateManager.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Create: `src/IGW.WebUi/src/views/Monitor.i18n.test.cjs`
- Create: `src/IGW.WebUi/src/views/MesFlow.i18n.test.cjs`

**Interfaces:**
- Consumes: global `$t()` and `useI18n().t`.
- Produces: bilingual station cards, anomaly lists, canvas toolbar, runtime states, node settings, templates, pickers and dialogs while preserving node/user names.

- [ ] **Step 1: Write failing route contracts**

Assert representative monitor and flow copy uses translation: `异常优先`, `搜索工位名称`, `清空筛选`, `保存并应用`, `添加节点`, `运行状态`, `等待运行数据`, `应用`, and `取消`.

- [ ] **Step 2: Run the route contracts and verify RED**

Run: `node --test src/views/Monitor.i18n.test.cjs src/views/MesFlow.i18n.test.cjs`.

- [ ] **Step 3: Migrate fixed copy and status formatters**

Translate fixed operator copy and IGW-owned runtime states. Keep task names, DB paths, node custom titles, PLC values, endpoint responses, and diagnostic raw details unchanged.

- [ ] **Step 4: Run all Monitor/MES tests**

Run: `node --test src/views/Monitor*.test.cjs src/views/MesFlow*.test.cjs src/components/*.test.cjs`.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.WebUi/src/views/Monitor.vue src/IGW.WebUi/src/views/MesFlow.vue src/IGW.WebUi/src/components src/IGW.WebUi/src/i18n/catalog.ts
git commit -m "feat: translate monitor and MES flow UI"
```

### Task 5: IGW settings route and catalog completeness

**Files:**
- Modify: `src/IGW.WebUi/src/views/GatewaySettings.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Create: `src/IGW.WebUi/src/views/GatewaySettings.i18n.test.cjs`
- Create: `src/IGW.WebUi/src/i18n/catalog.test.cjs`

**Interfaces:**
- Consumes: global `$t()` and `useI18n().t`.
- Produces: bilingual runtime/network/license settings and a catalog parity guard.

- [ ] **Step 1: Write failing settings and parity tests**

Check representative settings copy (`IGW 设置`, `运行状态`, `网络适配器`, `授权`, `激活`) and assert every declared Chinese catalog key has a non-empty English value.

- [ ] **Step 2: Run tests and verify RED**

Run: `node --test src/views/GatewaySettings.i18n.test.cjs src/i18n/catalog.test.cjs`.

- [ ] **Step 3: Migrate settings and complete English catalog**

Translate fixed labels, help text, validation and confirmation messages. Keep adapter names, IP addresses, project name, machine code, license code and server exception details unchanged.

- [ ] **Step 4: Run all Web UI node tests**

Run: `node --test "src/**/*.test.cjs"` using PowerShell-expanded test paths if the Node version does not expand globs.

- [ ] **Step 5: Commit**

```powershell
git add src/IGW.WebUi/src/views/GatewaySettings.vue src/IGW.WebUi/src/views/GatewaySettings.i18n.test.cjs src/IGW.WebUi/src/i18n
git commit -m "feat: translate IGW settings UI"
```

### Task 6: Type/build/browser verification and installer

**Files:**
- Modify generated output under `src/IGW.Runtime/wwwroot` through the existing Vite build only.
- Create installer output through `installer/build.bat` only after browser verification.

**Interfaces:**
- Consumes: completed bilingual Web UI.
- Produces: verified embedded Web UI and current IGW installer.

- [ ] **Step 1: Run all Web UI tests**

Run all `*.test.cjs` files with Node's test runner. Expected: zero failures.

- [ ] **Step 2: Run type checking and production build**

Run from `src/IGW.WebUi`:

```powershell
pnpm exec vue-tsc --noEmit
pnpm build
```

Expected: both commands exit 0 and Vite writes the embedded UI to `src/IGW.Runtime/wwwroot`.

- [ ] **Step 3: Browser verification**

Open `http://localhost:48020/#/project`, switch to English, and verify the three formal routes plus the MES flow dialog. Reload and confirm English remains. Switch to Chinese and confirm data/tree/canvas state does not reset. Capture both language screenshots.

- [ ] **Step 4: Build the installer without unrelated source changes**

Run `installer\build.bat` and verify the produced installer version/path. Do not claim portability from compilation alone; launch the installed Web UI and repeat the language persistence check.

- [ ] **Step 5: Final diff and commit**

Run `git diff --check`, inspect `git status --short`, then commit only the bilingual feature and generated embedded Web UI files with message `feat: add Chinese and English Web UI`.
