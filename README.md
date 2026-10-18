# 창자리 (WindowDock)

**창마다 원하는 자리를 저장하고, 다시 열면 그 자리에.**

여러 프로그램 창의 위치와 크기를 배치별로 저장하는 Windows 트레이 앱입니다. 버튼으로 배치를 전환하고, 이후 새로 열린 창은 저장된 자리에 한 번 자동 배치합니다.

[실행 파일 다운로드](https://github.com/Choi-sungjin/WindowDock/releases/latest) · [사용법](docs/사용법.md) · [검증 범위](docs/검증.md)

![창자리 관리 화면](docs/images/windowdock.png)

*시험용 창 3개로 확인한 실제 화면입니다. 개인 배치 데이터는 포함하지 않았습니다.*

## 시작하기

1. Releases에서 **WindowDock-v0.1.0-win-x64.zip**을 받아 원하는 폴더에 압축을 풉니다.
2. **WindowLayoutManager.exe**를 실행합니다. 설치 프로그램이나 별도의 .NET 런타임은 필요하지 않습니다.
3. 창들을 원하는 위치와 크기로 놓고 **현재 창에서 새로 저장**을 누릅니다.
4. 포함할 창과 배치 이름을 정한 뒤 저장합니다.
5. 배치를 선택하고 **지금 적용 + 자동 배치 시작**을 누릅니다.

**현재 열려 있는 창을 움직이지 않고 감시만 시작하려면**, 저장된 배치를 선택해 **활성 배치로 선택**을 누르고, 자동 배치가 꺼져 있을 때 **자동 배치 시작**을 누르세요.

## 어떻게 동작하나요?

| 상황 | 동작 |
| --- | --- |
| 배치 적용 버튼을 누름 | 현재 열려 있고 확실하게 구분되는 창을 저장 위치로 이동 |
| 프로그램이 닫혀 있음 | 실행하지 않고 저장된 자리를 유지 |
| 일치하는 창이 새로 열림 | 자동 배치가 켜져 있으면 한 번 이동 |
| 배치된 창을 직접 옮김 | 수동으로 옮긴 위치와 크기를 유지 |
| 창을 닫음 | 빈자리를 다른 창으로 채우거나 늘리지 않음 |
| 창자리를 다시 실행함 | 기존 창을 이동하지 않고 저장된 활성 배치·감시 상태 복원 |
| 후보 창이 여러 개라 구분할 수 없음 | 임의로 배치하지 않고 결과에 표시 |

같은 프로그램의 여러 창은 **항목 편집(세부) → 타이틀 포함**으로 구분할 수 있습니다. 모니터 해상도·입력·PBP 설정은 변경하지 않습니다.

## 트레이와 로그인 자동 실행

메인 창을 닫으면 트레이에서 계속 실행됩니다. 트레이 아이콘을 두 번 누르면 화면이 열리고, 메뉴에서 배치 적용·자동 배치 일시정지·종료를 할 수 있습니다.

로그인할 때 트레이에서 시작하려면 작업 스케줄러에 실행 파일과 `--start-in-tray` 인수를 등록하세요. [설정 방법](docs/사용법.md#로그인할-때-트레이에서-자동-실행)을 따라 한 번 등록하면 됩니다. ZIP을 내려받아 실행하는 것만으로 자동 시작 항목을 만들지는 않습니다.

## 저장 데이터와 호환성

- 배치: `%APPDATA%\WindowLayoutManager\profiles\`
- 활성 배치와 감시 설정: `%APPDATA%\WindowLayoutManager\settings.json`
- 기존 **창 배치 매니저**의 데이터를 그대로 사용합니다. 호환성을 위해 실행 파일명과 저장 폴더명은 `WindowLayoutManager`를 유지합니다.
- 사용자 배치, 창 제목, 개인 실행 경로, 계정 설정 및 개발 대화 로그는 이 저장소와 배포 ZIP에 포함하지 않습니다.

## 소스에서 빌드하기

Windows와 **.NET 10 SDK**가 필요합니다. 외부 NuGet 패키지 없이 Windows Forms와 Win32 API를 사용합니다.

저장소 루트의 PowerShell에서:

```powershell
dotnet build .\src\WindowLayoutManager\WindowLayoutManager.csproj -c Release

dotnet publish .\src\WindowLayoutManager\WindowLayoutManager.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false -o .\artifacts\publish
```

배포할 때는 `artifacts\publish\WindowLayoutManager.exe`와 사용 문서만 새 폴더에 복사하면 됩니다. 사용자 데이터는 실행 파일 옆이 아닌 위의 AppData 경로에 저장됩니다.

### 자체 검사

```powershell
New-Item -ItemType Directory -Force .\artifacts\checks | Out-Null
dotnet .\src\WindowLayoutManager\bin\Release\net10.0-windows\WindowLayoutManager.dll --self-test --output .\artifacts\checks\self-test.json
if ($LASTEXITCODE -ne 0) { throw 'Self-test failed' }
```

추가 GUI·트레이·실제 시험 창 검증 명령과 확인한 한계는 [검증 문서](docs/검증.md)에 정리했습니다.

## 프로젝트 구성

```text
src/WindowLayoutManager/
  App/        한국어 Windows Forms 화면과 트레이
  Services/   배치 저장, 창 매칭, 새 창 감시
  Native/     창 열거·이동 및 Windows API
  Model/      배치·창 정보 모델
  Cli/        명령행 기능과 검사 도구
docs/         사용법과 검증 범위
```

문제가 있으면 [Issues](https://github.com/Choi-sungjin/WindowDock/issues)에 재현 순서와 기대 동작을 알려주세요. 진단 파일에는 창 제목과 실행 경로가 들어갈 수 있으므로 공유 전에 내용을 확인해 주세요.
