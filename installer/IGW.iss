; IGW 工业网关 安装脚本(Inno Setup 6)
; 自包含发布(已内置 .NET 10 运行时,目标机无需另装任何环境)。
; 用 ISCC.exe 编译生成单个 setup exe;见 build.bat。
; 纯网页版:只装 Runtime(网关,注册为 Windows 服务「IGW Gateway」开机自启+崩溃自愈,内嵌网页 UI)。
; 首次安装可设置网页端口、OPC UA 端口和数据存储(SQLite / SQL Server / PostgreSQL / MySQL)；升级沿用原设置。
; 安装时自动清掉旧版残留的 WPF 托盘自启/快捷方式,并整体清空旧 dll(根除 WPF/WinForms 孤儿)后再铺新程序。

#define AppName "IGW 工业网关"
; 版本号从 version.txt 读取(每次打包前 bump_version.ps1 自增补丁号)
#define VerFile FileOpen(AddBackslash(SourcePath) + "version.txt")
#define AppVer  Trim(FileRead(VerFile))
#expr FileClose(VerFile)
#ifndef Dist
  #define Dist "..\dist\IGW"
#endif
#ifndef DbSetupDist
  #define DbSetupDist "..\dist\IGW.DbSetup"
#endif
#ifndef DataMoverDist
  #define DataMoverDist "..\dist\IGW.DataMover"
#endif
#if !FileExists(AddBackslash(SourcePath) + Dist + "\igw_s7plus.dll")
  #error "缺少 igw_s7plus.dll；请先执行 native/igw-s7plus 的 Release 构建。"
#endif
#if !FileExists(AddBackslash(SourcePath) + DataMoverDist + "\IGW.DataMover.exe")
  #error "缺少 IGW.DataMover.exe；请先发布 src/IGW.DataMover(见 build.bat)。"
#endif
#if !FileExists(AddBackslash(SourcePath) + DbSetupDist + "\IGW.DbSetup.exe")
  #error "缺少 IGW.DbSetup.exe；请先发布 src/IGW.DbSetup(见 build.bat)。"
#endif

[Setup]
; 固定 AppId = 程序身份;重复安装时 Inno 据此识别"已安装过"→走更新(原地覆盖、复用原安装路径)
AppId={{6F9A2B1C-3D4E-4F5A-8B6C-7D8E9F0A1B2C}
AppName={#AppName}
AppVersion={#AppVer}
DefaultDirName={autopf}\IGW
UsePreviousAppDir=yes
; auto:首次安装显示"选择目标位置/开始菜单"页(可自选路径);检测到已安装→跳过,用原路径更新
DisableDirPage=auto
DisableProgramGroupPage=auto
DefaultGroupName=IGW
OutputBaseFilename=IGW-Setup-{#AppVer}
OutputDir=.\out
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
; 覆盖前由 [Code] 的 PrepareUpgrade 自行结束本安装目录下的进程,这里不用 Inno 的重启管理器
CloseApplications=no
UninstallDisplayName={#AppName}
; 浅色皮肤：Windows 11 风格控件 + 整窗背景图(白到浅绿渐变，右下角设备连线图案和 IGW 水印)，
; 标题栏一起换样式；安装页进度条下面逐行列出安装细节
WizardStyle=modern windows11 hidebevels includetitlebar
SetupIconFile=..\assets\igw.ico
UninstallDisplayIcon={app}\IGW.Runtime.exe
; 图片由 make_wizard_images.py 从 assets/igw.ico 生成，按屏幕缩放挑最合适的一张
; 背景色设成白色会让各层面板透明：安装时透出背景图，卸载时透出 InitializeUninstallProgressForm 铺的背景图
WizardBackColor=white
WizardBackImageFile=wizard-back-596.png,wizard-back-994.png,wizard-back-1272.png
WizardSmallImageFile=wizard-small-100.png,wizard-small-150.png,wizard-small-200.png
WizardImageFile=wizard-large-100.bmp,wizard-large-150.bmp,wizard-large-200.bmp

[Languages]
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[Messages]
; 窗口左下角的产品名和版本号
BeveledLabel=IGW {#AppVer}

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式:"

[Dirs]
; 可写数据目录:工程/授权/历史/日志/证书都写这里(不写安装目录——普通用户对 Program Files 无写权限,
; 且卸载/重装会删安装目录导致配置丢失)。users-modify=所有用户可读写,支持服务账户与普通用户混用。
; 卸载不会删除本目录(Inno 只删自己装的),重装保留客户配置与授权。
; 只在首次安装(目录还不存在)时设置权限。已存在时不再重设：Windows 会把继承权限逐个下发到目录里
; 每个文件，接口报文多的现场(一小时几十万个文件)升级会卡在 "Creating directories" 几个小时，
; 而此时网关服务已停、旧版程序已清理。首次安装设过的权限一直有效，升级无需重设。
Name: "{commonappdata}\IGW"; Permissions: users-modify; Check: DataDirMissing

[Files]
; 纯网页版:只装 Runtime(自包含,含 .NET 运行时 + 内嵌网页 wwwroot)。WPF 的 Configurator/Tray 已退役,排除掉不装。
; dist 只含程序,绝不含用户数据(工程/授权/db),故重装不会覆盖配置。
Source: "{#Dist}\*"; DestDir: "{app}"; Excludes: "IGW.Configurator.exe,IGW.Tray.exe,*.igwproj,*.key,*.pem,*.db,*.db-wal,*.db-shm,private.key,issued-codes*.jsonl"; Flags: recursesubdirs ignoreversion
; 数据库设置工具：向导里“测试连接”时解压到临时目录运行，安装完成后复制到程序目录供日后使用。
Source: "{#DbSetupDist}\IGW.DbSetup.exe"; Flags: dontcopy
; 数据迁移工具：把整个数据目录搬到其它盘，原位置留目录链接(C 盘被日志、报文撑满时用)
Source: "{#DataMoverDist}\IGW.DataMover.exe"; DestDir: "{app}\tools"; Flags: ignoreversion
; 卸载界面的背景图：WizardBackImageFile 只对安装程序生效，卸载程序在 InitializeUninstallProgressForm 里从这里读
Source: "wizard-back-*.png"; DestDir: "{app}\setup"; Flags: ignoreversion

[Icons]
; 纯网页版:配置入口 = 浏览器打开本机 Runtime 服务的网页 UI(端口为安装时设置或网关实际监听的端口)
Name: "{group}\IGW 网关配置(网页)"; Filename: "http://localhost:{code:GetWebPort}/"
Name: "{group}\IGW 数据迁移工具"; Filename: "{app}\tools\IGW.DataMover.exe"; Comment: "把网关数据整体搬到其它盘"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\IGW 网关配置"; Filename: "http://localhost:{code:GetWebPort}/"; Tasks: desktopicon

[Run]
; Runtime 已由 [Code] 注册成 Windows 服务并启动(CurStepChanged 里已等它起来);装完自动打开网页配置界面
; runasoriginaluser:以普通用户身份开浏览器(安装器是提权的,不加这个浏览器会以管理员开、部分浏览器拒跑)
Filename: "http://localhost:{code:GetWebPort}/"; Description: "打开 IGW 网页配置界面(浏览器)"; Flags: shellexec nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; 卸载前只结束本安装目录下的 IGW 进程，不影响同一台电脑上其他目录的 IGW(如测试实例)
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -Command ""Get-Process IGW.Runtime,IGW.Tray,IGW.Configurator,IGW.LogService,IGW.DbSetup -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{app}\*' } | Stop-Process -Force"""; Flags: runhidden; RunOnceId: "KillAppProcesses"

[Code]
// 服务名(与 Runtime 代码里 UseWindowsService 的 ServiceName 一致)
const SVC = 'IGW Gateway';
const LOG_SVC = 'IGW Log Service';
const FW_RULE = 'IGW Gateway Runtime TCP';

// [Dirs] 数据目录权限只在首次安装时设置(见 [Dirs] 段说明)
function DataDirMissing: Boolean;
begin
  Result := not DirExists(ExpandConstant('{commonappdata}\IGW'));
end;

var
  LogDirPage: TInputDirWizardPage;
  PortPage: TInputQueryWizardPage;
  DbPage: TWizardPage;
  DbKind: TNewComboBox;
  DbLocal, DbRemote: TNewRadioButton;
  DbHost, DbPort, DbName, DbUser: TNewEdit;
  DbPass: TPasswordEdit;
  DbHostLabel, DbPortLabel, DbNameLabel, DbUserLabel, DbPassLabel, DbKindLabel, DbWhereLabel: TNewStaticText;
  DbTestButton: TNewButton;
  DbResult: TNewStaticText;
  DbTestPassed: Boolean;
  InstallLog: TNewListBox;
  LastStatus, LastFile: String;
  LoggingFiles: Boolean;
  IsUpgrade: Boolean;

// ---------------------------------------------------------------- 通用工具

function DataDir(): String;
begin
  Result := ExpandConstant('{commonappdata}\IGW');
end;

function JsonEscape(const S: String): String;
var i: Integer; c: Char;
begin
  Result := '';
  for i := 1 to Length(S) do
  begin
    c := S[i];
    if c = '\' then Result := Result + '\\'
    else if c = '"' then Result := Result + '\"'
    else if c = #13 then Result := Result + '\r'
    else if c = #10 then Result := Result + '\n'
    else if c = #9 then Result := Result + '\t'
    else if Ord(c) < 32 then Result := Result + ' '
    else Result := Result + c;
  end;
end;

// 从简单 JSON 中取字符串字段(工具输出的格式固定，只需处理常见转义)
function JsonString(const Json, Key: String): String;
var p, i: Integer; c: Char;
begin
  Result := '';
  p := Pos('"' + Key + '":"', Json);
  if p = 0 then Exit;
  i := p + Length(Key) + 4;
  while i <= Length(Json) do
  begin
    c := Json[i];
    if c = '"' then Exit;
    if (c = '\') and (i < Length(Json)) then
    begin
      i := i + 1;
      c := Json[i];
      if c = 'n' then c := #10
      else if c = 'r' then c := #13
      else if c = 't' then c := #9;
    end;
    Result := Result + c;
    i := i + 1;
  end;
end;

// 从简单 JSON 中取整数字段
function JsonInt(const Json, Key: String; Default: Integer): Integer;
var p, i: Integer; digits: String;
begin
  Result := Default;
  p := Pos('"' + Key + '":', Json);
  if p = 0 then Exit;
  i := p + Length(Key) + 3;
  while (i <= Length(Json)) and (Json[i] = ' ') do i := i + 1;
  digits := '';
  while (i <= Length(Json)) and (Json[i] >= '0') and (Json[i] <= '9') do
  begin
    digits := digits + Json[i];
    i := i + 1;
  end;
  Result := StrToIntDef(digits, Default);
end;

function ReadTextFile(const FileName: String): String;
var Lines: TArrayOfString; i: Integer;
begin
  Result := '';
  if not LoadStringsFromFile(FileName, Lines) then Exit;
  for i := 0 to GetArrayLength(Lines) - 1 do Result := Result + Lines[i];
end;

function WriteUtf8(const FileName, Text: String; Append: Boolean): Boolean;
var Lines: TArrayOfString;
begin
  SetArrayLength(Lines, 1);
  Lines[0] := Text;
  Result := SaveStringsToUTF8File(FileName, Lines, Append);
end;

function RunHidden(const FileName, Params: String): Integer;
var ResultCode: Integer;
begin
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then Result := ResultCode
  else Result := -1;
end;

function PowerShell(const Command: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "' + Command + '"');
end;

// 安装细节列表加一行(同时写进安装日志)，并滚到最新一行
procedure AddDetail(const Line: String);
begin
  Log(Line);
  if InstallLog = nil then Exit;
  InstallLog.Items.Add(Line);
  // 滚到最新一行(不选中，免得留下高亮)
  SendMessage(InstallLog.Handle, $0197 { LB_SETTOPINDEX }, InstallLog.Items.Count - 1, 0);
  InstallLog.Refresh;
end;

// 安装页显示当前步骤：步骤名进细节列表(同一步骤只记一次)，Detail 显示在进度条上方(如已等待秒数)
procedure ShowStep(const Title, Detail: String);
begin
  if Title <> LastStatus then
  begin
    LastStatus := Title;
    AddDetail('IGW：' + Title);
  end;
  WizardForm.StatusLabel.Caption := Title;
  WizardForm.FilenameLabel.Caption := Detail;
  WizardForm.StatusLabel.Update;
  WizardForm.FilenameLabel.Update;
end;

// 在安装页进度条下面放细节列表，和常见安装程序一样逐行显示正在做什么、正在复制哪个文件
procedure CreateInstallLog();
var Gauge: TNewProgressBar;
begin
  Gauge := WizardForm.ProgressGauge;
  InstallLog := TNewListBox.Create(WizardForm);
  InstallLog.Parent := WizardForm.InstallingPage;
  InstallLog.Left := Gauge.Left;
  InstallLog.Top := Gauge.Top + Gauge.Height + ScaleY(12);
  InstallLog.Width := Gauge.Width;
  InstallLog.Height := WizardForm.InstallingPage.Height - InstallLog.Top;
end;

// 复制文件阶段：把 Inno 的状态和正在解压的文件逐行记进细节列表
procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
var Status, FileName: String;
begin
  if not LoggingFiles then Exit;
  Status := WizardForm.StatusLabel.Caption;
  if (Status <> '') and (Status <> LastStatus) then
  begin
    LastStatus := Status;
    AddDetail(Status);
  end;
  FileName := WizardForm.FilenameLabel.Caption;
  if (FileName <> '') and (FileName <> LastFile) then
  begin
    LastFile := FileName;
    AddDetail('解压：' + FileName);
  end;
end;

// ---------------------------------------------------------------- 端口

// 网页端口：首次安装取向导里填写的值；升级时取网关上次实际监听的端口(runtime-endpoint.json)，都没有则 48020
function GetWebPort(Param: String): String;
var Json: String;
begin
  if (not IsUpgrade) and (PortPage <> nil) then
  begin
    Result := Trim(PortPage.Values[0]);
    if Result <> '' then Exit;
  end;
  Json := ReadTextFile(DataDir() + '\runtime-endpoint.json');
  Result := IntToStr(JsonInt(Json, 'restPort', 48020));
end;

function PortInUse(Port: Integer): Boolean;
begin
  Result := PowerShell('if (Get-NetTCPConnection -LocalPort ' + IntToStr(Port) +
    ' -State Listen -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }') = 1;
end;

function ValidatePorts(): Boolean;
var Web, Ua: Integer;
begin
  Result := False;
  Web := StrToIntDef(Trim(PortPage.Values[0]), -1);
  Ua := StrToIntDef(Trim(PortPage.Values[1]), -1);
  if (Web < 1) or (Web > 65535) or (Ua < 1) or (Ua > 65535) then
  begin
    MsgBox('端口必须是 1–65535 之间的数字。', mbError, MB_OK);
    Exit;
  end;
  if Web = Ua then
  begin
    MsgBox('网页端口和 OPC UA 端口不能相同。', mbError, MB_OK);
    Exit;
  end;
  if PortInUse(Web) then
  begin
    MsgBox('网页端口 ' + IntToStr(Web) + ' 已被本机其他程序占用，请换一个端口。', mbError, MB_OK);
    Exit;
  end;
  if PortInUse(Ua) then
  begin
    MsgBox('OPC UA 端口 ' + IntToStr(Ua) + ' 已被本机其他程序占用，请换一个端口。', mbError, MB_OK);
    Exit;
  end;
  Result := True;
end;

// ---------------------------------------------------------------- 数据库

function KindKey(): String;
begin
  case DbKind.ItemIndex of
    1: Result := 'sqlServer';
    2: Result := 'postgreSql';
    3: Result := 'mySql';
  else
    Result := 'sqlite';
  end;
end;

function KindDefaultPort(Index: Integer): String;
begin
  case Index of
    1: Result := '1433';
    2: Result := '5432';
    3: Result := '3306';
  else
    Result := '';
  end;
end;

function KindDefaultUser(Index: Integer): String;
begin
  case Index of
    1: Result := 'sa';
    2: Result := 'postgres';
    3: Result := 'root';
  else
    Result := '';
  end;
end;

function IsSqlite(): Boolean;
begin
  Result := DbKind.ItemIndex <= 0;
end;

procedure ResetDbTest();
begin
  DbTestPassed := False;
  // 创建页面时给输入框赋初值也会触发 OnChange，此时结果标签还没创建
  if DbResult <> nil then DbResult.Caption := '';
end;

procedure UpdateDbControls();
var UseServer: Boolean;
begin
  UseServer := not IsSqlite();
  DbWhereLabel.Enabled := UseServer;
  DbLocal.Enabled := UseServer;
  DbRemote.Enabled := UseServer;
  DbHostLabel.Enabled := UseServer and DbRemote.Checked;
  DbHost.Enabled := UseServer and DbRemote.Checked;
  if UseServer and DbLocal.Checked then DbHost.Text := '127.0.0.1';
  DbPortLabel.Enabled := UseServer;
  DbPort.Enabled := UseServer;
  DbNameLabel.Enabled := UseServer;
  DbName.Enabled := UseServer;
  DbUserLabel.Enabled := UseServer;
  DbUser.Enabled := UseServer;
  DbPassLabel.Enabled := UseServer;
  DbPass.Enabled := UseServer;
  DbTestButton.Enabled := UseServer;
  if not UseServer then
  begin
    DbResult.Font.Color := clGreen;
    DbResult.Caption := 'SQLite 是网关内置的本机数据库，无需安装或填写连接信息。';
  end;
end;

procedure DbKindChanged(Sender: TObject);
begin
  DbPort.Text := KindDefaultPort(DbKind.ItemIndex);
  DbUser.Text := KindDefaultUser(DbKind.ItemIndex);
  ResetDbTest();
  UpdateDbControls();
end;

procedure DbWhereChanged(Sender: TObject);
begin
  if DbRemote.Checked and (DbHost.Text = '127.0.0.1') then DbHost.Text := '';
  ResetDbTest();
  UpdateDbControls();
end;

procedure DbFieldChanged(Sender: TObject);
begin
  ResetDbTest();
end;

function DbRequestJson(): String;
begin
  Result := '{"kind":"' + KindKey() + '","host":"' + JsonEscape(Trim(DbHost.Text)) +
    '","port":' + IntToStr(StrToIntDef(Trim(DbPort.Text), 0)) +
    ',"database":"' + JsonEscape(Trim(DbName.Text)) + '","username":"' + JsonEscape(Trim(DbUser.Text)) +
    '","password":"' + JsonEscape(DbPass.Text) + '"}';
end;

// 运行 IGW.DbSetup：参数(含密码)写入临时文件，不出现在命令行；返回是否成功，Message 为中文说明
function RunDbSetup(const Command, ExtraArgs: String; var Message: String): Boolean;
var ToolPath, RequestPath, ResultPath, Json: String; Code: Integer;
begin
  Result := False;
  Message := '';
  ExtractTemporaryFile('IGW.DbSetup.exe');
  ToolPath := ExpandConstant('{tmp}\IGW.DbSetup.exe');
  RequestPath := ExpandConstant('{tmp}\igw-db-request.json');
  ResultPath := ExpandConstant('{tmp}\igw-db-result.json');
  DeleteFile(ResultPath);
  if not WriteUtf8(RequestPath, DbRequestJson(), False) then
  begin
    Message := '无法写入临时文件。';
    Exit;
  end;
  Code := RunHidden(ToolPath, Command + ' --request "' + RequestPath + '" --result "' + ResultPath + '"' + ExtraArgs);
  DeleteFile(RequestPath);
  Json := ReadTextFile(ResultPath);
  DeleteFile(ResultPath);
  Message := JsonString(Json, 'message');
  if Message = '' then Message := '数据库设置工具运行失败(返回代码 ' + IntToStr(Code) + ')。';
  Result := (Code = 0) and (Pos('"ok":true', Json) > 0);
end;

procedure DbTestClicked(Sender: TObject);
var Message: String;
begin
  DbTestButton.Enabled := False;
  DbResult.Font.Color := clBlack;
  DbResult.Caption := '正在测试连接，请稍候…';
  WizardForm.Refresh();
  try
    DbTestPassed := RunDbSetup('test', '', Message);
  finally
    DbTestButton.Enabled := True;
  end;
  if DbTestPassed then DbResult.Font.Color := clGreen else DbResult.Font.Color := clRed;
  DbResult.Caption := Message;
end;

function AddLabel(Page: TWizardPage; const Caption: String; Top: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(Page);
  Result.Parent := Page.Surface;
  Result.Caption := Caption;
  Result.Top := Top + ScaleY(3);
  Result.Left := 0;
end;

function AddEdit(Page: TWizardPage; Top: Integer): TNewEdit;
begin
  Result := TNewEdit.Create(Page);
  Result.Parent := Page.Surface;
  Result.Top := Top;
  Result.Left := ScaleX(110);
  Result.Width := Page.SurfaceWidth - ScaleX(110);
  Result.OnChange := @DbFieldChanged;
end;

procedure CreateDatabasePage();
var Top, Row: Integer;
begin
  DbPage := CreateCustomPage(PortPage.ID, '选择数据存储',
    '工程配置、采集流程、IGW 设置和运行数据保存到哪个数据库');
  Row := ScaleY(30);
  Top := 0;

  DbKindLabel := AddLabel(DbPage, '数据库类型：', Top);
  DbKind := TNewComboBox.Create(DbPage);
  DbKind.Parent := DbPage.Surface;
  DbKind.Style := csDropDownList;
  DbKind.Top := Top;
  DbKind.Left := ScaleX(110);
  DbKind.Width := DbPage.SurfaceWidth - ScaleX(110);
  DbKind.Items.Add('SQLite（默认，网关内置，无需安装）');
  DbKind.Items.Add('SQL Server');
  DbKind.Items.Add('PostgreSQL');
  DbKind.Items.Add('MySQL');
  DbKind.ItemIndex := 0;
  DbKind.OnChange := @DbKindChanged;
  Top := Top + Row;

  DbWhereLabel := AddLabel(DbPage, '数据库位置：', Top);
  DbLocal := TNewRadioButton.Create(DbPage);
  DbLocal.Parent := DbPage.Surface;
  DbLocal.Caption := '本机';
  DbLocal.Top := Top + ScaleY(2);
  DbLocal.Left := ScaleX(110);
  DbLocal.Width := ScaleX(80);
  DbLocal.Checked := True;
  DbLocal.OnClick := @DbWhereChanged;
  DbRemote := TNewRadioButton.Create(DbPage);
  DbRemote.Parent := DbPage.Surface;
  DbRemote.Caption := '远程电脑';
  DbRemote.Top := Top + ScaleY(2);
  DbRemote.Left := ScaleX(200);
  DbRemote.Width := ScaleX(120);
  DbRemote.OnClick := @DbWhereChanged;
  Top := Top + Row;

  DbHostLabel := AddLabel(DbPage, '服务器地址：', Top);
  DbHost := AddEdit(DbPage, Top);
  DbHost.Text := '127.0.0.1';
  Top := Top + Row;
  DbPortLabel := AddLabel(DbPage, '端口：', Top);
  DbPort := AddEdit(DbPage, Top);
  Top := Top + Row;
  DbNameLabel := AddLabel(DbPage, '数据库名：', Top);
  DbName := AddEdit(DbPage, Top);
  DbName.Text := 'igw';
  Top := Top + Row;
  DbUserLabel := AddLabel(DbPage, '账号：', Top);
  DbUser := AddEdit(DbPage, Top);
  Top := Top + Row;
  DbPassLabel := AddLabel(DbPage, '密码：', Top);
  DbPass := TPasswordEdit.Create(DbPage);
  DbPass.Parent := DbPage.Surface;
  DbPass.Top := Top;
  DbPass.Left := ScaleX(110);
  DbPass.Width := DbPage.SurfaceWidth - ScaleX(110);
  DbPass.OnChange := @DbFieldChanged;
  Top := Top + Row;

  DbTestButton := TNewButton.Create(DbPage);
  DbTestButton.Parent := DbPage.Surface;
  DbTestButton.Caption := '测试连接';
  DbTestButton.Top := Top;
  DbTestButton.Left := ScaleX(110);
  DbTestButton.Width := ScaleX(100);
  DbTestButton.Height := ScaleY(25);
  DbTestButton.OnClick := @DbTestClicked;
  Top := Top + ScaleY(32);

  DbResult := TNewStaticText.Create(DbPage);
  DbResult.Parent := DbPage.Surface;
  DbResult.Top := Top;
  DbResult.Left := 0;
  DbResult.Width := DbPage.SurfaceWidth;
  DbResult.AutoSize := False;
  DbResult.Height := ScaleY(40);
  DbResult.WordWrap := True;

  UpdateDbControls();
end;

// 重装时按原来的 database.json 预填(密码是加密的，需要重新输入)
procedure PrefillDatabasePage();
var Json, Kind, Host: String;
begin
  Json := ReadTextFile(DataDir() + '\database.json');
  if Json = '' then Exit;
  Kind := JsonString(Json, 'kind');
  if Kind = 'sqlServer' then DbKind.ItemIndex := 1
  else if Kind = 'postgreSql' then DbKind.ItemIndex := 2
  else if Kind = 'mySql' then DbKind.ItemIndex := 3
  else Exit;
  Host := JsonString(Json, 'host');
  if (Host = '127.0.0.1') or (Lowercase(Host) = 'localhost') then DbLocal.Checked := True else DbRemote.Checked := True;
  DbHost.Text := Host;
  DbPort.Text := IntToStr(JsonInt(Json, 'port', StrToIntDef(KindDefaultPort(DbKind.ItemIndex), 0)));
  DbName.Text := JsonString(Json, 'database');
  DbUser.Text := JsonString(Json, 'username');
  ResetDbTest();
  UpdateDbControls();
  DbResult.Font.Color := clBlack;
  DbResult.Caption := '已按原来的数据库设置预填，请重新输入密码并测试连接。';
end;

// ---------------------------------------------------------------- 向导页面

function ServiceExists(): Boolean;
begin
  // sc query 返回 0=存在,1060=不存在
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), 'query "' + SVC + '"') = 0;
end;

procedure InitializeWizard();
begin
  // 程序和服务还在 = 升级：沿用原端口和原数据库，不再询问。
  // 卸载后重装(数据目录保留)仍显示端口和数据库页，并用上次的设置预填。
  IsUpgrade := ServiceExists();

  LogDirPage := CreateInputDirPage(wpSelectDir,
    '选择日志存储位置',
    '指定网关运行日志的保存目录',
    '建议使用容量充足的本地磁盘。升级安装会保留原有日志目录，也可在 IGW 日志中心内安全迁移。',
    False, '');
  LogDirPage.Add('');
  LogDirPage.Values[0] := ExpandConstant('{commonappdata}\IGW\observability');

  PortPage := CreateInputQueryPage(LogDirPage.ID,
    '设置服务端口',
    '网页配置界面和 OPC UA 服务使用的端口',
    '请确认端口未被本机其他程序占用，并在防火墙/交换机上放行。安装后也可以在 IGW 设置中修改。');
  PortPage.Add('网页端口（浏览器访问 http://本机IP:端口/）：', False);
  PortPage.Add('OPC UA 服务端口（opc.tcp://本机IP:端口）：', False);
  PortPage.Values[0] := IntToStr(JsonInt(ReadTextFile(DataDir() + '\runtime-endpoint.json'), 'restPort', 48020));
  PortPage.Values[1] := IntToStr(JsonInt(ReadTextFile(DataDir() + '\runtime-endpoint.json'), 'opcUaPort', 4840));

  CreateDatabasePage();
  PrefillDatabasePage();
  CreateInstallLog();
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = LogDirPage.ID then
    Result := FileExists(DataDir() + '\observability.json')
  else if (PageID = PortPage.ID) or (PageID = DbPage.ID) then
    Result := IsUpgrade;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = PortPage.ID then
    Result := ValidatePorts()
  else if (CurPageID = DbPage.ID) and (not IsSqlite()) and (not DbTestPassed) then
  begin
    MsgBox('请先点击“测试连接”，连接成功后才能继续安装。' + #13#10#13#10 +
      '如果选择本机数据库，本机必须已经安装并启动了该数据库服务。', mbInformation, MB_OK);
    Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if not IsUpgrade then
  begin
    Result := Result + '服务端口:' + NewLine +
      Space + '网页端口 ' + Trim(PortPage.Values[0]) + NewLine +
      Space + 'OPC UA 端口 ' + Trim(PortPage.Values[1]) + NewLine + NewLine;
    if IsSqlite() then
      Result := Result + '数据存储:' + NewLine + Space + 'SQLite（本机内置）' + NewLine + NewLine
    else
      Result := Result + '数据存储:' + NewLine + Space + DbKind.Text + '  ' + Trim(DbHost.Text) + ':' + Trim(DbPort.Text) +
        '/' + Trim(DbName.Text) + NewLine + NewLine;
  end
  else
    Result := Result + '升级安装：沿用原有端口、数据库和工程配置。' + NewLine + NewLine;
  if MemoGroupInfo <> '' then Result := Result + MemoGroupInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo;
end;

// ---------------------------------------------------------------- 安装步骤

function ServiceStopped(): Boolean;
begin
  Result := RunHidden(ExpandConstant('{cmd}'), '/c sc query "' + SVC + '" | find "STOPPED" >nul') = 0;
end;

function ServiceRunning(): Boolean;
begin
  Result := RunHidden(ExpandConstant('{cmd}'), '/c sc query "' + SVC + '" | find "RUNNING" >nul') = 0;
end;

// 停止网关服务：按秒显示等待时间，停稳后才覆盖文件(最多等 60 秒)
procedure StopServiceWithProgress();
var i: Integer;
begin
  if not ServiceExists() then Exit;
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop "' + SVC + '"');
  for i := 0 to 120 do
  begin
    if ServiceStopped() then Exit;
    ShowStep('正在停止网关服务…', Format('正在等待采集和 OPC UA 服务安全退出，已等待 %d 秒', [i div 2]));
    Sleep(500);
  end;
end;

// 只结束本安装目录下的 IGW 进程，不影响同一台电脑上其他目录运行的 IGW(测试实例等)
procedure KillAppProcesses();
begin
  PowerShell('Get-Process IGW.Runtime,IGW.Tray,IGW.Configurator,IGW.LogService,IGW.DbSetup -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' } | Stop-Process -Force');
end;

procedure StopLogService();
begin
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop "' + LOG_SVC + '"');
  Sleep(2000);
end;

function LogServiceExists(): Boolean;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), 'query "' + LOG_SVC + '"') = 0;
end;

// 退役 WPF:纯网页版不装 WPF;装时顺手清掉旧版残留的托盘/配置工具「开机自启 + 快捷方式 + 程序本体」
procedure CleanOldWpf();
begin
  DeleteFile(ExpandConstant('{commonstartup}\IGW 网关.lnk'));
  DeleteFile(ExpandConstant('{group}\IGW 网关(托盘).lnk'));
  DeleteFile(ExpandConstant('{group}\IGW 配置工具.lnk'));
  DeleteFile(ExpandConstant('{commondesktop}\IGW 网关.lnk'));
  DeleteFile(ExpandConstant('{commondesktop}\IGW 网关(托盘).lnk'));
  DeleteFile(ExpandConstant('{commondesktop}\IGW 配置工具.lnk'));
  DeleteFile(ExpandConstant('{app}\IGW.Configurator.exe'));
  DeleteFile(ExpandConstant('{app}\IGW.Tray.exe'));
  DeleteFile(ExpandConstant('{app}\IGW.Configurator.deps.json'));
  DeleteFile(ExpandConstant('{app}\IGW.Configurator.runtimeconfig.json'));
  DeleteFile(ExpandConstant('{app}\IGW.Configurator.pdb'));
  DeleteFile(ExpandConstant('{app}\IGW.Tray.deps.json'));
  DeleteFile(ExpandConstant('{app}\IGW.Tray.runtimeconfig.json'));
  DeleteFile(ExpandConstant('{app}\IGW.Tray.pdb'));
end;

// 防火墙规则由安装器统一管理：按程序放行 TCP，兼容用户修改 REST/OPC UA 端口。
procedure RemoveFirewallRule();
begin
  if RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="' + FW_RULE + '"') <> 0 then
    Log('删除 IGW 防火墙规则失败或规则尚不存在');
end;

procedure RefreshFirewallRule();
begin
  RemoveFirewallRule();
  if RunHidden(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="' + FW_RULE + '" dir=in action=allow program="' +
    ExpandConstant('{app}\IGW.Runtime.exe') + '" enable=yes profile=any protocol=TCP edge=no') <> 0 then
    Log('创建 IGW 防火墙规则失败；安装继续');
end;

// 递归清掉 {app} 下所有 .dll —— [Files] 随后会从 dist 把 Runtime 需要的 dll 全量铺回
procedure RemoveDllsRec(const Dir: String; var Count: Integer);
var FR: TFindRec;
begin
  if FindFirst(Dir + '\*', FR) then
  begin
    try
      repeat
        if (FR.Name <> '.') and (FR.Name <> '..') then
        begin
          if (FR.Attributes and 16) <> 0 then
            RemoveDllsRec(Dir + '\' + FR.Name, Count)
          else if Lowercase(ExtractFileExt(FR.Name)) = '.dll' then
          begin
            DeleteFile(Dir + '\' + FR.Name);
            Count := Count + 1;
            if (Count mod 20) = 0 then
              ShowStep('正在清理旧版本组件…', Format('已清理 %d 个旧组件', [Count]));
          end;
        end;
      until not FindNext(FR);
    finally
      FindClose(FR);
    end;
  end;
end;

// 旧版的独立日志服务(IGW Log Service + OpenObserve)已退役：停掉、删除服务并移除程序目录。
procedure RetireLogService();
begin
  if LogServiceExists() then
  begin
    StopLogService();
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete "' + LOG_SVC + '"');
  end;
  PowerShell('Get-Process IGW.LogService,openobserve -ErrorAction SilentlyContinue | Where-Object { $_.Path -like ''' +
    ExpandConstant('{app}') + '\*'' } | Stop-Process -Force');
  DelTree(ExpandConstant('{app}\logservice'), True, True, True);
end;

// 重复安装=更新:覆盖文件前先停服务 + 结束本目录进程 + 清空旧 dll,每一步都记进安装细节
// (在安装页复制文件之前执行，和后面的复制、注册服务显示在同一个细节列表里)
procedure PrepareUpgrade();
var Count: Integer;
begin
  AddDetail('输出目录：' + ExpandConstant('{app}'));
  ShowStep('正在停止网关服务…', '');
  StopServiceWithProgress();
  ShowStep('正在退役旧版日志服务…', '');
  RetireLogService();
  ShowStep('正在结束残留进程…', '只结束本安装目录下的 IGW 程序');
  KillAppProcesses();
  ShowStep('正在清理旧版本组件…', '');
  Count := 0;
  RemoveDllsRec(ExpandConstant('{app}'), Count);
  if Count > 0 then AddDetail(Format('IGW：已清理 %d 个旧组件', [Count]));
  ShowStep('准备完成，开始复制新版本文件…', '');
  LoggingFiles := True;
end;

// 网关健康检查：返回 HTTP 状态码，连不上返回 0
function HealthStatus(var Body: String): Integer;
var WinHttp: Variant;
begin
  Result := 0;
  Body := '';
  try
    WinHttp := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    WinHttp.Open('GET', 'http://127.0.0.1:' + GetWebPort('') + '/api/v1/health', False);
    WinHttp.SetTimeouts(1000, 1000, 2000, 2000);
    WinHttp.Send('');
    Result := WinHttp.Status;
    Body := WinHttp.ResponseText;
  except
    Result := 0;
  end;
end;

// 等网关真正起来：百万点工程或首次同步外部数据库需要更久，按秒显示；最多等 5 分钟，超时不影响安装完成
procedure WaitForGateway();
var i, Status: Integer; Body, Detail: String;
begin
  for i := 0 to 600 do
  begin
    Status := HealthStatus(Body);
    if (Status = 200) or (Status = 401) then
    begin
      if Pos('"databaseState":"syncing"', Body) = 0 then Exit;
      Detail := Format('正在创建数据表并同步数据，已等待 %d 秒', [i div 2]);
    end
    else if not ServiceRunning() and (i > 20) then
      Detail := Format('服务尚未运行，正在重试，已等待 %d 秒', [i div 2])
    else
      Detail := Format('正在载入工程并启动采集，已等待 %d 秒（大型工程首次启动需要更长时间）', [i div 2]);
    ShowStep('正在启动网关…', Detail);
    Sleep(500);
  end;
  Log('等待网关启动超时；服务仍在后台继续启动');
end;

// 首次安装：配置数据库(建库并保存连接)；失败时可重试，或改用本机 SQLite 继续安装
procedure ConfigureDatabase();
var Message: String; Choice: Integer;
begin
  if IsUpgrade then Exit;
  while True do
  begin
    if RunDbSetup('configure', ' --data-dir "' + DataDir() + '"', Message) then Exit;
    if IsSqlite() then Exit;
    Choice := MsgBox('数据库配置失败：' + #13#10 + Message + #13#10#13#10 +
      '点“重试”再试一次；点“取消”改用本机 SQLite 继续安装(之后可重新安装再选择数据库)。',
      mbError, MB_RETRYCANCEL);
    if Choice <> IDRETRY then
    begin
      DbKind.ItemIndex := 0;
      RunDbSetup('configure', ' --data-dir "' + DataDir() + '"', Message);
      Exit;
    end;
  end;
end;

// 首次安装：把向导里填的端口交给网关，网关首次启动时写入工程设置
procedure WriteFirstRunPorts();
begin
  if IsUpgrade then Exit;
  WriteUtf8(DataDir() + '\first-run.json',
    '{"restPort":' + IntToStr(StrToIntDef(Trim(PortPage.Values[0]), 48020)) +
    ',"opcUaPort":' + IntToStr(StrToIntDef(Trim(PortPage.Values[1]), 4840)) + '}', False);
end;

// 安装后:把 Runtime 注册成 Windows 服务(开机自启 + 崩溃自愈),并启动
procedure CurStepChanged(CurStep: TSetupStep);
var ExePath, BinPath, InitArgs: String;
begin
  if CurStep = ssInstall then PrepareUpgrade();
  if CurStep <> ssPostInstall then Exit;
  LoggingFiles := False;
  ShowStep('正在清理旧版桌面工具…', '');
  CleanOldWpf();
  ExePath := ExpandConstant('{app}\IGW.Runtime.exe');
  BinPath := 'binPath= "\"' + ExePath + '\"" start= auto';

  ShowStep('正在配置防火墙…', '放行网关程序的 TCP 端口');
  RefreshFirewallRule();

  ShowStep('正在初始化日志目录…', LogDirPage.Values[0]);
  RetireLogService();
  InitArgs := '--initialize-logs --data-dir "' + DataDir() + '" --log-dir "' + LogDirPage.Values[0] + '"';
  if RunHidden(ExePath, InitArgs) <> 0 then
    Log('日志存储目录初始化失败；网关启动时将使用默认目录。');

  ShowStep('正在配置数据库…', '检查连接并创建数据库');
  ExtractTemporaryFile('IGW.DbSetup.exe');
  CopyFile(ExpandConstant('{tmp}\IGW.DbSetup.exe'), ExpandConstant('{app}\IGW.DbSetup.exe'), False);
  ConfigureDatabase();

  ShowStep('正在写入端口设置…', '');
  WriteFirstRunPorts();

  ShowStep('正在注册网关服务…', '开机自启，异常退出后自动重启');
  if ServiceExists() then
    StopServiceWithProgress()
  else
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'create "' + SVC + '" ' + BinPath + ' DisplayName= "IGW 工业网关"');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'config "' + SVC + '" ' + BinPath);
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'description "' + SVC + '" "IGW 工业协议网关 Runtime(采集 / OPC UA / MES)"');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'failure "' + SVC + '" reset= 86400 actions= restart/5000/restart/10000/restart/30000');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'failureflag "' + SVC + '" 1');

  ShowStep('正在启动网关服务…', '');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'start "' + SVC + '"');

  WaitForGateway();
  ShowStep('网关已启动，正在完成安装…', '');
  AddDetail('IGW：安装完成');
  WizardForm.FilenameLabel.Caption := '';
end;

// 卸载界面和安装界面用同一张背景图(皮肤由 WizardStyle 统一控制，背景图要自己铺)：
// 按窗口宽度挑最接近的一张，铺满整个窗口并放到最底层
procedure InitializeUninstallProgressForm();
var Back: TBitmapImage; Width: Integer; FileName: String;
begin
  Width := UninstallProgressForm.ClientWidth;
  if Width <= 596 then FileName := 'wizard-back-596.png'
  else if Width <= 994 then FileName := 'wizard-back-994.png'
  else FileName := 'wizard-back-1272.png';
  FileName := ExpandConstant('{app}\setup\') + FileName;
  if not FileExists(FileName) then Exit;
  Back := TBitmapImage.Create(UninstallProgressForm);
  Back.Parent := UninstallProgressForm;
  Back.Align := alClient;
  Back.Stretch := True;
  Back.PngImage.LoadFromFile(FileName);
  Back.SendToBack;
  // 左下角版本号是不透明的白底：底色改成背景图在那个位置的浅绿，看不出方块
  UninstallProgressForm.BeveledLabel.Color := $F0F8EE;
end;

// 卸载:停 + 删服务(先于 [UninstallRun] 的进程清理)
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then Exit;
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop "' + SVC + '"');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop "' + LOG_SVC + '"');
  Sleep(2000);
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete "' + SVC + '"');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete "' + LOG_SVC + '"');
  RemoveFirewallRule();
end;
