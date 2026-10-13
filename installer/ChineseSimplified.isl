; *** Inno Setup 6.5.0+ 简体中文消息(IGW 自行翻译，对应 Default.isl) ***
;
; 注意：原文末尾没有句号的消息不要加句号，Inno Setup 会自动补。

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=936
DialogFontName=Microsoft YaHei UI
DialogFontSize=9
WelcomeFontName=Microsoft YaHei UI
WelcomeFontSize=12

[Messages]

; *** Application titles
SetupAppTitle=安装
SetupWindowTitle=%1 安装
UninstallAppTitle=卸载
UninstallAppFullTitle=%1 卸载

; *** Misc. common
InformationTitle=信息
ConfirmTitle=确认
ErrorTitle=错误

; *** SetupLdr messages
SetupLdrStartupMessage=现在将安装 %1。是否继续？
LdrCannotCreateTemp=无法创建临时文件，安装已中止
LdrCannotExecTemp=无法运行临时目录中的文件，安装已中止
HelpTextNote=

; *** Startup error messages
LastErrorMessage=%1。%n%n错误 %2：%3
SetupFileMissing=安装目录中缺少文件 %1。请修正该问题，或重新获取安装程序。
SetupFileCorrupt=安装文件已损坏。请重新获取安装程序。
SetupFileCorruptOrWrongVer=安装文件已损坏，或与当前安装程序版本不兼容。请修正该问题，或重新获取安装程序。
InvalidParameter=命令行传入了无效参数：%n%n%1
SetupAlreadyRunning=安装程序已在运行。
WindowsVersionNotSupported=本程序不支持当前电脑运行的 Windows 版本。
WindowsServicePackRequired=本程序需要 %1 Service Pack %2 或更高版本。
NotOnThisPlatform=本程序不能在 %1 上运行。
OnlyOnThisPlatform=本程序必须在 %1 上运行。
OnlyOnTheseArchitectures=本程序只能安装在以下处理器架构的 Windows 上：%n%n%1
WinVersionTooLowError=本程序需要 %1 %2 或更高版本。
WinVersionTooHighError=本程序不能安装在 %1 %2 或更高版本上。
AdminPrivilegesRequired=安装本程序需要以管理员身份登录。
PowerUserPrivilegesRequired=安装本程序需要以管理员或 Power Users 组成员身份登录。
SetupAppRunningError=安装程序检测到 %1 正在运行。%n%n请先关闭它的所有实例，然后点“确定”继续，或点“取消”退出。
UninstallAppRunningError=卸载程序检测到 %1 正在运行。%n%n请先关闭它的所有实例，然后点“确定”继续，或点“取消”退出。

; *** Startup questions
PrivilegesRequiredOverrideTitle=选择安装模式
PrivilegesRequiredOverrideInstruction=选择安装模式
PrivilegesRequiredOverrideText1=%1 可以为所有用户安装(需要管理员权限)，也可以只为你安装。
PrivilegesRequiredOverrideText2=%1 可以只为你安装，也可以为所有用户安装(需要管理员权限)。
PrivilegesRequiredOverrideAllUsers=为所有用户安装(&A)
PrivilegesRequiredOverrideAllUsersRecommended=为所有用户安装(推荐)(&A)
PrivilegesRequiredOverrideCurrentUser=只为我安装(&M)
PrivilegesRequiredOverrideCurrentUserRecommended=只为我安装(推荐)(&M)

; *** Misc. errors
ErrorCreatingDir=无法创建目录“%1”
ErrorTooManyFilesInDir=目录“%1”中的文件太多，无法再创建文件

; *** Setup common messages
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。现在退出，程序将不会被安装。%n%n你可以以后再运行安装程序完成安装。%n%n确定退出安装吗？
AboutSetupMenuItem=关于安装程序(&A)...
AboutSetupTitle=关于安装程序
AboutSetupMessage=%1 版本 %2%n%3%n%n%1 主页：%n%4
AboutSetupNote=
TranslatorNote=

; *** Buttons
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消(&C)
ButtonYes=是(&Y)
ButtonYesToAll=全部是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全部否(&O)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)

; *** "Select Language" dialog messages
SelectLanguageTitle=选择安装语言
SelectLanguageLabel=选择安装过程中使用的语言。

; *** Common wizard text
ClickNext=点“下一步”继续，或点“取消”退出安装。
BeveledLabel=
BrowseDialogTitle=浏览文件夹
BrowseDialogLabel=在下面的列表中选择一个文件夹，然后点“确定”。
NewFolderName=新建文件夹

; *** "Welcome" wizard page
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=现在将在你的电脑上安装 [name/ver]。%n%n建议继续之前关闭其他所有应用程序。

; *** "Password" wizard page
WizardPassword=密码
PasswordLabel1=本安装程序受密码保护。
PasswordLabel3=请输入密码，然后点“下一步”继续。密码区分大小写。
PasswordEditLabel=密码(&P)：
IncorrectPassword=输入的密码不正确，请重试。

; *** "License Agreement" wizard page
WizardLicense=许可协议
LicenseLabel=继续之前请阅读以下重要信息。
LicenseLabel3=请阅读以下许可协议。必须接受协议条款才能继续安装。
LicenseAccepted=我接受协议(&A)
LicenseNotAccepted=我不接受协议(&D)

; *** "Information" wizard pages
WizardInfoBefore=信息
InfoBeforeLabel=继续之前请阅读以下重要信息。
InfoBeforeClickLabel=准备好继续安装后，点“下一步”。
WizardInfoAfter=信息
InfoAfterLabel=继续之前请阅读以下重要信息。
InfoAfterClickLabel=准备好继续安装后，点“下一步”。

; *** "User Information" wizard page
WizardUserInfo=用户信息
UserInfoDesc=请输入你的信息。
UserInfoName=用户名(&U)：
UserInfoOrg=单位(&O)：
UserInfoSerial=序列号(&S)：
UserInfoNameRequired=必须输入用户名。

; *** "Select Destination Location" wizard page
WizardSelectDir=选择安装位置
SelectDirDesc=要把 [name] 安装到哪里？
SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹。
SelectDirBrowseLabel=点“下一步”继续。如果要选择其他文件夹，请点“浏览”。
DiskSpaceGBLabel=至少需要 [gb] GB 可用磁盘空间。
DiskSpaceMBLabel=至少需要 [mb] MB 可用磁盘空间。
CannotInstallToNetworkDrive=不能安装到网络驱动器。
CannotInstallToUNCPath=不能安装到 UNC 路径。
InvalidPath=必须输入带盘符的完整路径，例如：%n%nC:\APP%n%n或以下格式的 UNC 路径：%n%n\\server\share
InvalidDrive=选择的驱动器或 UNC 共享不存在或无法访问，请重新选择。
DiskSpaceWarningTitle=磁盘空间不足
DiskSpaceWarning=安装至少需要 %1 KB 可用空间，但所选驱动器只有 %2 KB 可用。%n%n仍要继续吗？
DirNameTooLong=文件夹名称或路径太长。
InvalidDirName=文件夹名称无效。
BadDirName32=文件夹名称不能包含以下字符：%n%n%1
DirExistsTitle=文件夹已存在
DirExists=文件夹：%n%n%1%n%n已经存在。仍要安装到该文件夹吗？
DirDoesntExistTitle=文件夹不存在
DirDoesntExist=文件夹：%n%n%1%n%n不存在。要创建该文件夹吗？

; *** "Select Components" wizard page
WizardSelectComponents=选择组件
SelectComponentsDesc=要安装哪些组件？
SelectComponentsLabel2=勾选要安装的组件，取消勾选不需要安装的组件。准备好后点“下一步”。
FullInstallation=完整安装
CompactInstallation=简洁安装
CustomInstallation=自定义安装
NoUninstallWarningTitle=组件已存在
NoUninstallWarning=安装程序检测到以下组件已安装在你的电脑上：%n%n%1%n%n取消勾选不会卸载这些组件。%n%n仍要继续吗？
ComponentSize1=%1 KB
ComponentSize2=%1 MB
ComponentsDiskSpaceGBLabel=当前选择至少需要 [gb] GB 磁盘空间。
ComponentsDiskSpaceMBLabel=当前选择至少需要 [mb] MB 磁盘空间。

; *** "Select Additional Tasks" wizard page
WizardSelectTasks=选择附加任务
SelectTasksDesc=要执行哪些附加任务？
SelectTasksLabel2=选择安装 [name] 时要执行的附加任务，然后点“下一步”。

; *** "Select Start Menu Folder" wizard page
WizardSelectProgramGroup=选择开始菜单文件夹
SelectStartMenuFolderDesc=要把程序的快捷方式放在哪里？
SelectStartMenuFolderLabel3=安装程序将在以下开始菜单文件夹中创建程序的快捷方式。
SelectStartMenuFolderBrowseLabel=点“下一步”继续。如果要选择其他文件夹，请点“浏览”。
MustEnterGroupName=必须输入文件夹名称。
GroupNameTooLong=文件夹名称或路径太长。
InvalidGroupName=文件夹名称无效。
BadGroupName=文件夹名称不能包含以下字符：%n%n%1
NoProgramGroupCheck2=不创建开始菜单文件夹(&D)

; *** "Ready to Install" wizard page
WizardReady=准备安装
ReadyLabel1=安装程序已准备好在你的电脑上安装 [name]。
ReadyLabel2a=点“安装”开始安装；如果要检查或修改设置，请点“上一步”。
ReadyLabel2b=点“安装”开始安装。
ReadyMemoUserInfo=用户信息：
ReadyMemoDir=安装位置：
ReadyMemoType=安装类型：
ReadyMemoComponents=已选组件：
ReadyMemoGroup=开始菜单文件夹：
ReadyMemoTasks=附加任务：

; *** TDownloadWizardPage wizard page and DownloadTemporaryFile
DownloadingLabel2=正在下载文件...
ButtonStopDownload=停止下载(&S)
StopDownload=确定要停止下载吗？
ErrorDownloadAborted=下载已中止
ErrorDownloadFailed=下载失败：%1 %2
ErrorDownloadSizeFailed=获取大小失败：%1 %2
ErrorProgress=进度无效：%1 / %2
ErrorFileSize=文件大小无效：应为 %1，实际为 %2

; *** TExtractionWizardPage wizard page and ExtractArchive
ExtractingLabel=正在解压文件...
ButtonStopExtraction=停止解压(&S)
StopExtraction=确定要停止解压吗？
ErrorExtractionAborted=解压已中止
ErrorExtractionFailed=解压失败：%1

; *** Archive extraction failure details
ArchiveIncorrectPassword=密码不正确
ArchiveIsCorrupted=压缩包已损坏
ArchiveUnsupportedFormat=不支持的压缩包格式

; *** "Preparing to Install" wizard page
WizardPreparing=准备安装
PreparingDesc=安装程序正在准备在你的电脑上安装 [name]。
PreviousInstallNotCompleted=上一个程序的安装或卸载尚未完成，需要重启电脑才能完成。%n%n重启电脑后，请再次运行安装程序完成 [name] 的安装。
CannotContinue=安装无法继续。请点“取消”退出。
ApplicationsFound=以下应用程序正在使用安装程序需要更新的文件。建议允许安装程序自动关闭这些应用程序。
ApplicationsFound2=以下应用程序正在使用安装程序需要更新的文件。建议允许安装程序自动关闭这些应用程序。安装完成后，安装程序会尝试重新启动它们。
CloseApplications=自动关闭这些应用程序(&A)
DontCloseApplications=不关闭这些应用程序(&D)
ErrorCloseApplications=安装程序无法自动关闭所有应用程序。建议继续之前，先关闭所有正在使用需要更新文件的应用程序。
PrepareToInstallNeedsRestart=安装程序必须重启电脑。重启后请再次运行安装程序完成 [name] 的安装。%n%n现在重启吗？

; *** "Installing" wizard page
WizardInstalling=正在安装
InstallingLabel=[name] 正在安装，请等候。

; *** "Setup Completed" wizard page
FinishedHeadingLabel=[name] 安装完成
FinishedLabelNoIcons=[name] 已安装到你的电脑上。
FinishedLabel=[name] 已安装到你的电脑上，可以通过已创建的快捷方式启动。
ClickFinish=点“完成”退出安装。
FinishedRestartLabel=要完成 [name] 的安装，必须重启电脑。现在重启吗？
FinishedRestartMessage=要完成 [name] 的安装，必须重启电脑。%n%n现在重启吗？
ShowReadmeCheck=是，我要查看自述文件
YesRadio=是，现在重启电脑(&Y)
NoRadio=否，稍后再重启(&N)
RunEntryExec=运行 %1
RunEntryShellExec=查看 %1

; *** "Setup Needs the Next Disk" stuff
ChangeDiskTitle=需要下一张磁盘
SelectDiskLabel2=请插入磁盘 %1 并点“确定”。%n%n如果该磁盘上的文件在下面显示以外的文件夹中，请输入正确路径或点“浏览”。
PathLabel=路径(&P)：
FileNotInDir2=在“%2”中找不到文件“%1”。请插入正确的磁盘或选择其他文件夹。
SelectDirectoryLabel=请指定下一张磁盘的位置。

; *** Installation phase messages
SetupAborted=安装未完成。%n%n请修正问题后重新运行安装程序。
AbortRetryIgnoreSelectAction=选择操作
AbortRetryIgnoreRetry=重试(&T)
AbortRetryIgnoreIgnore=忽略错误并继续(&I)
AbortRetryIgnoreCancel=取消安装
RetryCancelSelectAction=选择操作
RetryCancelRetry=重试(&T)
RetryCancelCancel=取消

; *** Installation status messages
StatusClosingApplications=正在关闭应用程序...
StatusCreateDirs=正在创建目录...
StatusExtractFiles=正在解压文件...
StatusDownloadFiles=正在下载文件...
StatusCreateIcons=正在创建快捷方式...
StatusCreateIniEntries=正在写入 INI 项...
StatusCreateRegistryEntries=正在写入注册表...
StatusRegisterFiles=正在注册文件...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成安装...
StatusRestartingApplications=正在重新启动应用程序...
StatusRollback=正在撤销更改...

; *** Misc. errors
ErrorInternal2=内部错误：%1
ErrorFunctionFailedNoCode=%1 失败
ErrorFunctionFailed=%1 失败；代码 %2
ErrorFunctionFailedWithMessage=%1 失败；代码 %2。%n%3
ErrorExecutingProgram=无法运行文件：%n%1

; *** Registry errors
ErrorRegOpenKey=打开注册表项出错：%n%1\%2
ErrorRegCreateKey=创建注册表项出错：%n%1\%2
ErrorRegWriteKey=写入注册表项出错：%n%1\%2

; *** INI errors
ErrorIniEntry=在文件“%1”中写入 INI 项出错。

; *** File copying errors
FileAbortRetryIgnoreSkipNotRecommended=跳过这个文件(不推荐)(&S)
FileAbortRetryIgnoreIgnoreNotRecommended=忽略错误并继续(不推荐)(&I)
SourceIsCorrupted=源文件已损坏
SourceDoesntExist=源文件“%1”不存在
SourceVerificationFailed=源文件校验失败：%1
VerificationSignatureDoesntExist=签名文件“%1”不存在
VerificationSignatureInvalid=签名文件“%1”无效
VerificationKeyNotFound=签名文件“%1”使用了未知密钥
VerificationFileNameIncorrect=文件名不正确
VerificationFileTagIncorrect=文件标记不正确
VerificationFileSizeIncorrect=文件大小不正确
VerificationFileHashIncorrect=文件哈希不正确
ExistingFileReadOnly2=现有文件为只读，无法替换。
ExistingFileReadOnlyRetry=去掉只读属性后重试(&R)
ExistingFileReadOnlyKeepExisting=保留现有文件(&K)
ErrorReadingExistingDest=读取现有文件时出错：
FileExistsSelectAction=选择操作
FileExists2=文件已存在。
FileExistsOverwriteExisting=覆盖现有文件(&O)
FileExistsKeepExisting=保留现有文件(&K)
FileExistsOverwriteOrKeepAll=后续冲突都这样处理(&D)
ExistingFileNewerSelectAction=选择操作
ExistingFileNewer2=现有文件比安装程序要安装的文件更新。
ExistingFileNewerOverwriteExisting=覆盖现有文件(&O)
ExistingFileNewerKeepExisting=保留现有文件(推荐)(&K)
ExistingFileNewerOverwriteOrKeepAll=后续冲突都这样处理(&D)
ErrorChangingAttr=修改现有文件属性时出错：
ErrorCreatingTemp=在目标目录中创建文件时出错：
ErrorReadingSource=读取源文件时出错：
ErrorCopying=复制文件时出错：
ErrorDownloading=下载文件时出错：
ErrorExtracting=解压压缩包时出错：
ErrorReplacingExistingFile=替换现有文件时出错：
ErrorRestartReplace=重启替换失败：
ErrorRenamingTemp=在目标目录中重命名文件时出错：
ErrorRegisterServer=无法注册 DLL/OCX：%1
ErrorRegSvr32Failed=RegSvr32 失败，退出码 %1
ErrorRegisterTypeLib=无法注册类型库：%1

; *** Uninstall display name markings
UninstallDisplayNameMark=%1 (%2)
UninstallDisplayNameMarks=%1 (%2, %3)
UninstallDisplayNameMark32Bit=32 位
UninstallDisplayNameMark64Bit=64 位
UninstallDisplayNameMarkAllUsers=所有用户
UninstallDisplayNameMarkCurrentUser=当前用户

; *** Post-installation errors
ErrorOpeningReadme=打开自述文件时出错。
ErrorRestartingComputer=安装程序无法重启电脑，请手动重启。

; *** Uninstaller messages
UninstallNotFound=文件“%1”不存在，无法卸载。
UninstallOpenError=无法打开文件“%1”，无法卸载
UninstallUnsupportedVer=卸载日志文件“%1”的格式无法被当前版本的卸载程序识别，无法卸载
UninstallUnknownEntry=卸载日志中有未知项(%1)
ConfirmUninstall=确定要完全删除 %1 及其所有组件吗？
UninstallOnlyOnWin64=本程序只能在 64 位 Windows 上卸载。
OnlyAdminCanUninstall=只有管理员权限的用户才能卸载本程序。
UninstallStatusLabel=正在从你的电脑上删除 %1，请等候。
UninstalledAll=%1 已从你的电脑上成功删除。
UninstalledMost=%1 卸载完成。%n%n有些项目无法删除，可以手动删除。
UninstalledAndNeedsRestart=要完成 %1 的卸载，必须重启电脑。%n%n现在重启吗？
UninstallDataCorrupted=文件“%1”已损坏，无法卸载

; *** Uninstallation phase messages
ConfirmDeleteSharedFileTitle=删除共享文件？
ConfirmDeleteSharedFile2=系统显示以下共享文件已不再被任何程序使用。要删除这个共享文件吗？%n%n如果仍有程序在使用该文件，删除后这些程序可能无法正常工作。如果不确定，请选“否”。保留该文件不会造成任何问题。
SharedFileNameLabel=文件名：
SharedFileLocationLabel=位置：
WizardUninstalling=卸载状态
StatusUninstalling=正在卸载 %1...

; *** Shutdown block reasons
ShutdownBlockReasonInstallingApp=正在安装 %1。
ShutdownBlockReasonUninstallingApp=正在卸载 %1。

[CustomMessages]

NameAndVersion=%1 版本 %2
AdditionalIcons=附加快捷方式：
CreateDesktopIcon=创建桌面快捷方式(&D)
CreateQuickLaunchIcon=创建快速启动栏快捷方式(&Q)
ProgramOnTheWeb=%1 网站
UninstallProgram=卸载 %1
LaunchProgram=运行 %1
AssocFileExtension=将 %1 与 %2 文件扩展名关联(&A)
AssocingFileExtension=正在将 %1 与 %2 文件扩展名关联...
AutoStartProgramGroupDescription=启动：
AutoStartProgram=自动启动 %1
AddonHostProgramNotFound=在所选文件夹中找不到 %1。%n%n仍要继续吗？
