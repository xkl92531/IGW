# IGW Silent Firewall Rule Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the IGW installer silently allow `IGW.Runtime.exe` TCP inbound traffic on domain, private, and public Windows network profiles, refresh the rule on upgrade, and remove it on uninstall.

**Architecture:** Keep firewall ownership inside the existing Inno Setup script. Use one fixed rule name and a program-scoped `netsh advfirewall` rule so configured REST and OPC UA port changes remain supported; installation refreshes the rule before the Windows service starts, while uninstall removes only the installer-owned rule.

**Tech Stack:** Inno Setup 6 Pascal Script, Windows `netsh advfirewall`, Node.js built-in test runner, existing `installer/build.bat` packaging pipeline.

## Global Constraints

- Work directly on the single `master` branch as explicitly requested by the user.
- Allow TCP inbound traffic for `IGW.Runtime.exe` on `profile=any` and keep `edge=no`.
- Do not add UDP rules or fixed-port rules.
- Firewall command failures must be written to the Inno Setup log but must not abort installation or uninstall.
- Do not stage or overwrite unrelated existing MES and Web UI working-tree changes.

---

### Task 1: Installer-managed firewall lifecycle

**Files:**
- Create: `installer/IGW.firewall.test.cjs`
- Modify: `installer/IGW.iss`

**Interfaces:**
- Consumes: Inno Setup `{sys}\netsh.exe`, `{app}\IGW.Runtime.exe`, `CurStepChanged`, and `CurUninstallStepChanged`.
- Produces: `RemoveFirewallRule()` and `RefreshFirewallRule()` procedures managing the fixed rule `IGW Gateway Runtime TCP`.

- [ ] **Step 1: Write the failing source-contract test**

Create a Node test that reads `installer/IGW.iss` and asserts all of the following:

```javascript
assert.match(source, /FW_RULE\s*=\s*'IGW Gateway Runtime TCP'/)
assert.match(source, /profile=any protocol=TCP edge=no/)
assert.match(source, /program="' \+ ExePath \+ '"/)
assert.ok(source.indexOf('RefreshFirewallRule();') < source.indexOf("'start \"' + SVC"))
assert.match(uninstallSection, /RemoveFirewallRule\(\);/)
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
& 'C:\Users\Administrator\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' --test installer\IGW.firewall.test.cjs
```

Expected: FAIL because `IGW.iss` does not yet define or call the firewall lifecycle procedures.

- [ ] **Step 3: Implement the minimal Inno Setup behavior**

In `installer/IGW.iss`:

- Add `FW_RULE = 'IGW Gateway Runtime TCP'` beside the existing service constant.
- Add `RemoveFirewallRule()` that runs hidden:

```text
netsh advfirewall firewall delete rule name="IGW Gateway Runtime TCP"
```

- Add `RefreshFirewallRule()` that removes the old rule, then runs hidden:

```text
netsh advfirewall firewall add rule name="IGW Gateway Runtime TCP" dir=in action=allow program="{app}\IGW.Runtime.exe" enable=yes profile=any protocol=TCP edge=no
```

- Log non-zero command results without returning an installer error.
- Call `RefreshFirewallRule()` after resolving `ExePath` and before any `sc start` call.
- Call `RemoveFirewallRule()` during uninstall after deleting the Windows service.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the Node command from Step 2.

Expected: one passing test, zero failures.

- [ ] **Step 5: Compile the Inno Setup script through the packaging pipeline**

Run `installer\build.bat` with the bundled Node directory prepended to `PATH`. Do not pass `--no-bump`; the existing `1.5.60` version becomes `1.5.61`.

Expected: `installer\out\IGW-Setup-1.5.61.exe` is created and the compiler reports `Successful compile`.

- [ ] **Step 6: Run full verification**

Run:

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --no-restore --nologo
& 'C:\Users\Administrator\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' --test installer\IGW.firewall.test.cjs src\IGW.WebUi\src\views\*.test.cjs
```

Then copy `dist\IGW` to a temporary Chinese path, set `DOTNET_MULTILEVEL_LOOKUP=0` and an invalid `DOTNET_ROOT`, start `IGW.Runtime.exe` with an empty project, and verify `/api/v1/health` plus `/` both respond.

Expected: all .NET and Node tests pass; portable health is `ok`; web status is `200`.

- [ ] **Step 7: Commit the installer change**

```powershell
git add installer/IGW.iss installer/IGW.firewall.test.cjs installer/version.txt
git commit -m "feat: configure firewall silently during install"
```

Do not stage the generated installer or unrelated working-tree files.

