; Iris · AI 电脑体检 — Inno Setup 6 安装脚本
; 由 tools/build_app.py --installer 调用，不要直接编译：版本号和文件位置由脚本通过 /D 传入
;   AppVersion        1.0.0-beta.1
;   AppNumericVersion 1.0.0
;   StageDir          已准备好的文件：Iris.exe、LICENSE.txt、PRIVACY.txt、使用说明.txt
;   RepoRoot          仓库根目录（取图标）
;
; 设计：
;   · 只装给当前用户（不需要管理员），程序本身也不常驻、不装服务
;   · 安装前展示 AGPL-3.0 协议和隐私政策
;   · 自带卸载程序；卸载时询问是否同时删除体检报告和设置

#ifndef AppVersion
  #error 请通过 tools/build_app.py --installer 构建
#endif

#define AppName "Iris"
#define AppExe "Iris.exe"

[Setup]
AppId={{6F2B8C1E-4A7D-4E59-9B3F-1C8D2E7A5B40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductName=Iris · AI 电脑体检
VersionInfoDescription=Iris 安装程序
AppPublisher=Wilhelm
AppPublisherURL=https://github.com/WilhelmWei/Iris
AppSupportURL=https://github.com/WilhelmWei/Iris/issues
AppCopyright=Copyright (C) 2026 Wilhelm. Licensed under AGPL-3.0.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile={#StageDir}\LICENSE.txt
InfoBeforeFile={#StageDir}\PRIVACY.txt
SetupIconFile={#RepoRoot}\src\Pcdoctor.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} · AI 电脑体检
OutputBaseFilename=Iris-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 固定时间戳之外的内容由源码决定；安装程序本身的签名以后由 SignPath 完成（见 .github/workflows/release.yml）

[Languages]
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
en.DeleteData=Also delete Iris check-up reports, operation log and settings (%LOCALAPPDATA%\pcdoctor)?%n%nUninstalling does NOT undo changes you made with Iris. If you want to restore them, cancel now and use "Undo" in Iris first.
en.AdminData=Iris also keeps logs and backups of administrator operations in%n%1%nDeleting that folder requires administrator rights: use Settings → Clear all data in Iris before uninstalling, or delete it manually.
en.Launch=Launch Iris
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
zh.DeleteData=是否同时删除 Iris 的体检报告、操作记录和设置（%LOCALAPPDATA%\pcdoctor）？%n%n卸载不会撤销你用 Iris 做过的修改。如果想恢复，请先点"否"取消卸载，在 Iris 的「还原」里撤销后再卸载。
zh.AdminData=Iris 还在下面的文件夹里保存了管理员操作的记录和备份：%n%1%n删除它需要管理员权限：可以先在 Iris 的「设置 → 清除所有数据」里删除，或者手动删除。
zh.Launch=打开 Iris
#endif

[Files]
Source: "{#StageDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\PRIVACY.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AdminDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if MsgBox(ExpandConstant('{cm:DeleteData}'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\pcdoctor'), True, True, True);
    AdminDir := ExpandConstant('{commonpf64}\pcdoctor-data');
    if DirExists(AdminDir) then
      MsgBox(FmtMessage(ExpandConstant('{cm:AdminData}'), [AdminDir]), mbInformation, MB_OK);
  end;
end;
