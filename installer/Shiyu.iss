; 拾语安装包（ADR-0015）：Inno Setup 6.7，只为当前用户安装，不要管理员权限。
;
; 编译走 tools\installer\build-installer.ps1（发布流程、CI 与本机都用它）：
;   ISCC /DAppVersion=0.14.0 /DPublishDir=<dotnet publish 的输出> /O<输出目录> installer\Shiyu.iss
; 产物 shiyu-win-x64-setup.exe 与 shiyu-win-x64.zip 一起放在 Releases：zip 给想解压即用的人，
; 也是应用内自动更新下载的那一份——更新原地换掉安装目录里的文件，所以安装目录必须是当前用户
; 自己写得动的地方，这正是「只为我安装」的理由。
;
; 本文件与 ChineseSimplified.isl 都存为 UTF-8 带 BOM：里面有中文，ISCC 靠 BOM 认编码。

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
; 安装包在 Windows 里的身份：卸载项的键名（{AppId}_is1）、升级时认出旧版都靠它。永远不改——
; 拾语启动时也按它找到自己的卸载项、把版本号对齐（Shiyu.Windows InstallRegistration.AppId）。
AppId={{158C0196-252A-4126-8516-DC9C05C0B7BB}
AppName=拾语
AppVersion={#AppVersion}
AppVerName=拾语 {#AppVersion}
AppPublisher=youguoda
AppPublisherURL=https://github.com/youguoda/shiyu
AppSupportURL=https://github.com/youguoda/shiyu/issues
AppUpdatesURL=https://github.com/youguoda/shiyu/releases

; 只为我安装：默认装进当前用户的程序目录（%LOCALAPPDATA%\Programs\Shiyu），可以改到别处；
; 不弹管理员确认，也不提供"为所有用户安装"。升级时沿用上次的位置，不再问。
PrivilegesRequired=lowest
DefaultDirName={userpf}\Shiyu
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes

; 拾语开着就不装、不卸：它是托盘里的单实例程序，互斥量名与 App 的 SingleInstance 一致。
; SmokeAllowRunning 只给本机冒烟用（用户自己的拾语正开着时也能试装一份到临时目录）。
#ifndef SmokeAllowRunning
AppMutex=Shiyu
#endif
CloseApplications=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputBaseFilename=shiyu-win-x64-setup
SetupIconFile=..\src\Shiyu.App\Assets\app.ico
UninstallDisplayIcon={app}\Shiyu.App.exe
UninstallDisplayName=拾语
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ShowLanguageDialog=no

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "在桌面创建快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\拾语"; Filename: "{app}\Shiyu.App.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\拾语"; Filename: "{app}\Shiyu.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Shiyu.App.exe"; Description: "立即启动拾语"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 应用内更新可能带来安装时没有的文件：卸载时把整个安装文件夹清掉。安全的前提在选目录那一页——
; 只许装进新文件夹、空文件夹或原来的拾语文件夹（NextButtonClick）。
Type: filesandordirs; Name: "{app}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function IsDirEmpty(const Dir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
          Result := False;
      until (not Result) or (not FindNext(FindRec));
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ 选目录：卸载会清掉整个安装文件夹，所以不许装进放着别的东西的文件夹。 }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Dir := WizardDirValue;
    if DirExists(Dir) and (not FileExists(AddBackslash(Dir) + 'Shiyu.App.exe')) and (not IsDirEmpty(Dir)) then
    begin
      SuppressibleMsgBox('这个文件夹里已经有别的东西了。' + #13#10#13#10 +
        '卸载拾语时会删掉整个安装文件夹，请选一个新的或空的文件夹（比如在它下面再建一个 Shiyu）。',
        mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

{ 开机自启是拾语自己写的（当前用户的 Run 键）：只删指向本安装目录的那一项，别处的拾语不碰。 }
procedure RemoveOwnAutostart;
var
  Value, AppDir: String;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, 'Shiyu', Value) then
  begin
    Value := RemoveQuotes(Trim(Value));
    AppDir := AddBackslash(ExpandConstant('{app}'));
    if CompareText(Copy(Value, 1, Length(AppDir)), AppDir) = 0 then
      RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'Shiyu');
  end;
end;

{ 卸载：先去自启，再问要不要连数据一起删（默认「否」，静默卸载也是「否」）。删数据交给拾语自己
  （Shiyu.App.exe --remove-user-data）：它知道数据挪到了哪里、哪些文件是它的。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveOwnAutostart;
    if SuppressibleMsgBox('要同时删除拾语的数据吗？' + #13#10#13#10 +
        '包括剪贴板历史与图片、翻译记录、设置和保存的凭据。' + #13#10 +
        '选「否」会保留它们，以后重新安装拾语还能接着用。',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      if (not Exec(ExpandConstant('{app}\Shiyu.App.exe'), '--remove-user-data', '', SW_HIDE,
          ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
        SuppressibleMsgBox('有些数据没能删掉，可以手动删除这个文件夹：' + #13#10 +
          ExpandConstant('{localappdata}\Shiyu'), mbError, MB_OK, IDOK);
    end;
  end;
end;
