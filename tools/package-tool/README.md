# package-tool · IGW 打包工具(WinForms GUI)

一键「git 拉取最新代码 → 按目标平台构建 → 本地保存 / 上传服务器」。

## 启动

双击 `启动打包工具.bat`(内容为 ASCII,避免 cmd 代码页问题),或:

```powershell
powershell -STA -ExecutionPolicy Bypass -File tools\package-tool\IGW.PackageTool.ps1
```

自检(无 GUI,验证 DPAPI 加解密 / askpass 生成 / 端口探测等):

```powershell
powershell -ExecutionPolicy Bypass -File tools\package-tool\IGW.PackageTool.ps1 -SelfTest
```

## 功能

| 需求 | 实现 |
|---|---|
| 从 git 地址获取 | 填 git 地址:仓库不存在时自动 `clone`;存在且地址不同则 `set-url`;留空用仓库 origin。拉取用 `pull --ff-only`(不自动合并,防意外) |
| 需要账号密码弹窗、永久记住 | 静默尝试(系统缓存凭据,禁 GCM 弹框)→ 失败用已存凭据经 `GIT_ASKPASS` 静默重试 → 仍失败弹本工具的账号密码框,勾选"永久记住"后 **DPAPI(当前 Windows 用户)加密**保存 |
| 打包到服务器(需账号密码) | 探测 22(SSH)/445(SMB) 端口自动识别 Linux / Windows,可手动强制;服务器账号同样可永久记住 |
| Linux 服务器 | `deploy\package-docker.ps1` 出 Docker 部署包 zip → **Posh-SSH(SFTP)** 上传(缺模块时引导安装);可选勾选上传后自动部署(stop/rm 容器 + unzip + deploy.sh) |
| Windows 服务器 | `installer\build.bat` 出 `IGW-Setup-<版本>.exe` + sha256 → SMB(UNC)凭据映射盘拷贝;远程路径填 `\\服务器\共享\目录` 或服务器本地 `D:\目录`(自动转 `D$` 管理共享,需管理员) |
| 本地地址 | 选产物类型(exe / Docker zip / 两者),可选复制到指定目录(留空则产物留在 `installer\out`、`deploy\out`) |

## 数据保存位置

`%APPDATA%\IGW\PackageTool\`

- `config.json` —— 非敏感设置(仓库路径、git 地址、目标、上次输入)
- `git-credentials.dpapi` / `server-credentials.dpapi` —— **DPAPI 加密**的凭据(仅当前 Windows 用户可解密,换用户/重装系统即失效)
- `askpass.cmd` / `askpass.ps1` —— git 静默凭据助手(运行时生成,不含明文)

界面上「清除已存凭据」一键删除以上两个 .dpapi。

## 版本号

每次点「开始」都会先自增 `installer\version.txt`(补丁号 +1,满 100 进次版本),与 build.bat 的发布惯例一致;exe 构建用 `--no-bump` 防止二次自增。

## 注意

- 构建前置与 `installer\build.bat`、`deploy\package-docker.ps1` 相同:dotnet SDK 10、Node/pnpm、Inno Setup;Linux 包首次需拉基础镜像(podman 或 python 直拉),服务器无外网时不要勾 `-SkipBaseImage`。
- 拉取要求工作区干净(至少不与远程冲突);`version.txt` 因上次打包自增产生的本地改动不影响 pull,但建议按惯例提交 `chore: release installer <版本>`。
- Linux 自动部署会 **停止并删除现有 igw 容器** 后重跑 deploy.sh,igwdata 数据卷不受影响,仍请谨慎勾选。

## 故障排查

- **窗口意外关闭**:早期版本把 PowerShell scriptblock 直接挂在子进程输出事件上,回调发生在无 runspace 的线程池线程会导致进程崩溃;现已改为 C# 编译的事件泵(`LinePump`),并把全局未处理异常写入 `%APPDATA%\IGW\PackageTool\crash.log`(界面启动时会提示)。若再遇关闭,把 crash.log 发回即可定位。
- 子进程输出按 GBK(936)解码,匹配 build.bat/ISCC 中文控制台输出。
