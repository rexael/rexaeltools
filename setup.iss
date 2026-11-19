; Rexael Tools / Format Çarkı kurulum scripti (Inno Setup 6.3+)
; Önce şunu çalıştır:
;   dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
; Sonra bu dosyayı FormatCarki.csproj ile aynı klasörden Inno Setup ile derle.

#define MyAppName "Rexael Tools"
#define MyExe "FormatCarki.exe"
#define PublishDir "bin\Release\net8.0-windows\win-x64\publish"
; Sürümü exe'nin kendisinden okur (csproj'daki <Version>)
#define MyAppVersion GetVersionNumbersString(PublishDir + "\" + MyExe)

[Setup]
AppId={{B7C1E5A4-3D2F-4F6B-9A58-0E1A2B3C4D5E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Rexael
; Kullanıcı klasörüne (%LOCALAPPDATA%\Programs) kurulur -> yönetici izni gerekmez
; ve otomatik güncelleyici exe'yi değiştirebilir.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Uygulama açıksa kurulum/kaldırma uyarır (Program.cs'teki mutex adı)
AppMutex=FormatCarki.Single
OutputBaseFilename=RexaelTools-Setup-{#MyAppVersion}
OutputDir=installer
Compression=lzma2
SolidCompression=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyExe}
; SetupIconFile=Assets\logo.ico   ; .ico dosyan olursa aç

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "autostart"; Description: "Windows ile birlikte başlat"; Flags: unchecked
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyExe}"; DestDir: "{app}"; Flags: ignoreversion
; SkiaSharp'ın yerel kütüphanesi exe'nin içinde değil, yanında durmalı
Source: "{#PublishDir}\libSkiaSharp.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyExe}"; Tasks: desktopicon

[Registry]
; Uygulamadaki "Windows ile başlat" ayarıyla aynı kayıt (Program.cs > Autostart)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FormatCarki"; \
  ValueData: """{app}\{#MyExe}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart
; Kaldırırken uygulama ayarlarını da sil
Root: HKCU; Subkey: "Software\RexaelTools"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyExe}"; Description: "{#MyAppName} uygulamasını başlat"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Otomatik güncelleyicinin bıraktığı dosyalar
Type: files; Name: "{app}\{#MyExe}.old"
Type: files; Name: "{app}\{#MyExe}.new"

[Code]
// .NET 8 Desktop Runtime kurulu mu? (uygulama --self-contained false derlendiği için gerekli)
function IsDotNet8DesktopInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 2) = '8.' then
      begin
        Result := True;
        Exit;
      end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet8DesktopInstalled then
    if MsgBox('Rexael Tools için .NET 8 Desktop Runtime gerekiyor ve bilgisayarında bulunamadı.' + #13#10 +
              'İndirme sayfasını açayım mı? (Runtime kurulduktan sonra bu kurulumu tekrar çalıştır.)',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
      Result := False;
    end;
end;
