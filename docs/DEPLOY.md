# IGW 工业网关 · 部署说明(Windows / Linux + 网页端)

IGW 一套代码,Windows、Linux 都能跑;所有配置/监控/授权既可用桌面工具(仅 Windows),也可用**浏览器网页端**(两平台通用)。

---

## 一、程序与数据目录

| | 程序(升级会覆盖) | 数据(工程/授权/日志/证书,升级/重装不动) |
|---|---|---|
| Windows | `C:\Program Files\IGW` | `C:\ProgramData\IGW` |
| Linux | `/opt/igw` | `/var/lib/igw`(非 root 手跑时 `~/.local/share/igw`) |

环境变量 `IGW_DATA_DIR` 可覆盖数据目录(多实例隔离/测试)。

---

## 二、Windows 安装

1. 运行 `IGW-Setup-<版本>.exe`(自包含,目标机无需装 .NET)。
2. 安装完托盘自动起、自动拉起网关(可选开机自启)。
3. 管理:右下角托盘图标右键(配置工具 / 运行状态 / **授权·激活** / 启停),或用网页端(见下)。

---

## 三、Linux 安装

```bash
tar xzf igw-<版本>-linux-x64.tar.gz
cd igw
sudo ./install.sh        # 装 /opt/igw(程序) + /var/lib/igw(数据),注册 systemd 服务并开机自启
```

管理命令 `igwctl`:

```bash
igwctl status              # 运行状态(服务 + 健康)
igwctl start|stop|restart  # 启停
igwctl logs 200            # 看日志(journalctl)
igwctl license             # 看授权状态 + 本机机器码
igwctl activate <授权码>   # 粘授权码激活
```

崩溃/挂死由 systemd 自动拉起(等同 Windows 托盘看门狗)。

---

## 四、网页端(Windows/Linux 通用)

浏览器打开 **`http://网关IP:48020/`**(端口=工程 `settings.restPort`,默认 48020)。功能:

- **概览**:工程、标记数、设备在线、OPC 客户端 + 设备状态表(实时刷)。
- **工程配置**:通道/设备/组/点 树编辑、新建通道向导、导入导出 `.igwproj`、**保存并应用**(热重载)。多人同时编辑有冲突保护。
- **点位监控**:搜索点位、实时值、写值。
- **MES 上报**:运行状态看板(节点通过/失败/等待,与桌面一致)、上报日志、**流程画布**(节点/连线/多选/复制粘贴/运行态高亮)。
- **授权**:本机机器码(复制)+ 粘贴授权码激活(只有管理员能激活);顶部常驻授权徽标;列出授权的驱动和功能。

### 登录与权限

- 网页必须登录。首次安装内置唯一管理员 **admin / 初始密码 admin**,第一次登录强制改密码。
- 管理员在顶部菜单「用户与权限」(只有管理员能看到)新建普通用户,给每个页面(监控中心、工程配置、采集流程、采集性能、日志中心、IGW 设置)分配「不可见 / 只能看 / 可修改」;可重置密码、停用、删除。管理员不能删除,也不能再建第二个。
- 只能看的页面顶部显示「只读」,修改会被网关拒绝;不可见的页面菜单里不显示。
- 账号存在网关数据目录的 `accounts.json`(不属于工程,导入导出工程不会带走),登录凭证 7 天有效,改密码/改权限后旧凭证立即失效。
- 忘记管理员密码:停服务后删除数据目录里的 `accounts.json`,重启后恢复 admin / admin(普通用户需要重建)。
- 第三方系统调用的**数据接口不需要登录**:读写点位(`/api/v1/tags/*`、`/api/v1/values`)、实时订阅、`/iotgateway/*`、DCS 兼容接口 `/Tag/*`、`/api/v1/health`。工程配置、用户、授权等管理接口必须登录(`Authorization: Bearer <登录返回的 token>`)。

---

## 五、授权(两平台一套)

1. 现场读**本机机器码**(托盘/网页「授权」页 或 `igwctl license` 或 `GET /api/v1/license`)。
2. 发给厂商,厂商用发码工具(图形版「IGW 发码工具」或命令行 `IgwKeygen issue -c 客户名 -m 机器码 -d 天数 --drivers Siemens.S7,Modbus.Tcp --features Mes`)生成授权码。
   发码时勾选**授权驱动**和**授权功能**(MES 采集上报、OPC UA 服务、MQTT 发布、历史存储):
   - 没授权的驱动:新建通道时不能选;已有通道保留配置但不采集,名称标「未授权」。
   - 没授权的功能:不启动;「采集流程」页面不显示。
   - 旧授权码和 7 天试用没有这两项限制,全部可用。模拟器驱动不受限制。
3. 现场管理员登录网页,在「IGW 设置 → 授权与激活」粘贴激活。换了驱动/功能不同的授权码,网关自动重启采集让新授权生效。

一码一机:授权码只在对应机器码的机器上有效。断网无影响(全本地验证)。重装系统/换系统盘会导致机器码变,需重发。

- Windows 机器码 = 注册表 MachineGuid + 系统盘卷序列号。
- Linux 机器码 = `/etc/machine-id`(容器里可能是随机值,授权版建议裸机/虚机部署)。

---

## 六、协议对接(北向)

- **OPC UA**:`opc.tcp://网关IP:4840`(DCS/SCADA/MES)。
- **REST**:`http://网关IP:48020/api/v1/*`(读写点、设备状态、工程下发…)。
- **KEP IoT Gateway 兼容**:`/iotgateway/read|write|browse`——原来对接 KEPServerEX IoT Gateway(39320)的系统换个地址即可直连。
- **MQTT**(工程启用时):按 `igw/{通道}/{设备}/{组}/{点}` 发布。

---

## 七、从源码构建

```bash
# Windows 安装包(需 .NET 10 SDK + Inno Setup 6 + node/pnpm)
installer\build.bat

# Linux 包(Git Bash / WSL,需 .NET 10 SDK + node/pnpm)
installer/build-linux.sh
```

两个脚本都会先 `pnpm build` 把网页端(`src/IGW.WebUi`)构建进 `wwwroot`,再自包含发布。
