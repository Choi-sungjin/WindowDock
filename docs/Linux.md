# 창자리 · WindowDock Linux 0.3.0

Ubuntu 24.04 x64의 **X11(Xorg) 세션**을 지원합니다. Python 3.10 이상, GTK 3, libwnck 3을 사용합니다. 다른 Linux 배포판·Wayland는 검증 대상이 아닙니다. Wayland에서는 실행을 거부하므로 로그인 화면의 세션 메뉴에서 **Ubuntu on Xorg**를 선택하세요.

## 다운로드와 설치

1. [Releases](https://github.com/Choi-sungjin/WindowDock/releases/latest)에서 **WindowDock-v0.3.0-linux-x64.tar.gz**를 받습니다. Windows 사용자는 **WindowDock-v0.3.0-win-x64.zip**을 선택하세요.
2. Linux 압축을 풀고 폴더 안에서 실행합니다.

```bash
tar -xzf WindowDock-v0.3.0-linux-x64.tar.gz
cd WindowDock-v0.3.0-linux-x64
./install.sh
~/.local/bin/windowdock
```

설치에 관리자 권한은 필요하지 않습니다. 의존성이 없다면 먼저 아래 패키지를 설치하세요. pip나 가상 환경은 필요하지 않습니다.

```bash
sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0
# 선택: Ubuntu 트레이 통합 (상태 아이콘을 지원하는 데스크톱 환경도 필요)
sudo apt install gir1.2-ayatanaappindicator3-0.1
```

앱 메뉴에서 **창자리 (WindowDock)**를 검색해 실행할 수도 있습니다. 설치 없이 압축 폴더의 `./launch.sh`로 실행할 수 있습니다. 소스 체크아웃에서는 `./linux/install.sh`, `./linux/launch.sh`를 사용하세요.

## 사용 순서

1. 저장하려는 창을 원하는 위치·크기로 놓습니다.
2. **현재 창에서 새로 저장** → 이름 입력 → 포함할 창 체크 → 저장.
3. **지금 적용 + 자동 배치 시작**을 누르면 확실히 일치하는 현재 창을 복원하고 이후 열린 창을 한 번 배치합니다.
4. 감시만 켜려면 **활성 배치로 선택** → **자동 배치 시작 / 정지**를 누릅니다. 이 과정에서는 이미 열린 창을 옮기지 않습니다.
5. **슬롯 설정**에서 자주 쓰는 배치를 1~8번 버튼에 지정합니다. 이전·다음 버튼은 이름순으로 배치를 순환합니다.

**저장 위치 갱신**은 확실히 일치하는 열린 창만 갱신하며, 닫힌 창과 애매한 항목은 유지합니다. **선택 항목 편집**에서 역할·제목 포함 조건·좌표·크기·최대화를 바꿀 수 있습니다. 프로그램을 자동 실행하지 않으며 빈자리도 다른 창으로 채우지 않습니다.

같은 실행 파일과 창 클래스를 가진 창이 여러 개면 제목 포함 조건으로 구분하세요. 똑같은 후보를 구분할 수 없으면 임의로 선택하지 않습니다. 전체 화면 창은 건너뛰며, 앱 자체의 최소 크기 등으로 이동이 거부되면 성공으로 표시하지 않습니다. 창의 최대화 여부를 저장·복원하지만 최대화 이전의 숨겨진 복원 크기는 보장하지 않습니다. 다른 작업 공간의 창도 인식하되 작업 공간 자체를 이동시키지는 않습니다.

## Windows와 Linux 차이

| 기능 | Windows | Linux 0.3.0 |
| --- | --- | --- |
| 저장·수동 적용·새 창 자동 배치 | 지원 | X11에서 지원 |
| 8개 슬롯 / 이전·다음 버튼 | 지원 | 지원 |
| 앱 밖에서 누르는 전역 단축키 | 설정 가능 | 미지원, 버튼 사용 |
| 트레이 | 지원 | Ayatana typelib + 상태 아이콘 지원 환경에서만 |
| 창 닫기 | 트레이로 숨김 | 트레이 있으면 숨김, 없으면 완전히 종료 |
| 로그인 자동 실행 | 별도 설정 | 별도 설정 |
| 저장 위치 | AppData | XDG 사용자 데이터 폴더 |

트레이를 사용할 수 없는 환경에서는 숨겨진 채 실행하지 않습니다. `--start-in-tray`를 주어도 관리 창이 나타납니다. 트레이 없이 자동 배치를 계속 사용하려면 앱을 최소화하세요. **완전히 종료**는 감시도 종료합니다. 앱을 다시 실행하면 저장된 활성 배치와 감시 상태를 복원하지만 기존 창은 이동시키지 않습니다.

## 데이터·업데이트·제거

- 앱: `~/.local/lib/windowdock/`
- 실행 명령: `~/.local/bin/windowdock`
- 앱 메뉴: `~/.local/share/applications/windowdock.desktop`
- 개인 배치: `${XDG_DATA_HOME:-~/.local/share}/WindowDock/profiles/`
- 설정과 슬롯: 같은 데이터 폴더의 `settings.json`
- Windows 배치는 실행 파일 경로와 창 클래스가 달라 자동 가져오지 않습니다.

새 버전에서도 `./install.sh`를 다시 실행하면 됩니다. 개인 데이터는 그대로 유지됩니다. 실행 중인 앱을 **완전히 종료**한 후 다시 열면 새 코드가 적용됩니다. 다운로드에 개인 배치·창 제목·개인 경로는 포함하지 않습니다.

```bash
./install.sh --uninstall
```

위 명령은 앱과 메뉴만 제거하고 개인 배치는 남깁니다. 손상되거나 지원하지 않는 배치 파일은 읽지 않고 원본을 보존합니다. 손상된 `settings.json`은 실행을 중단합니다. 원본을 별도로 백업하고 데이터 폴더에서 옮긴 뒤 재실행하면 기본 설정을 만듭니다.

로그인 자동 실행을 원할 때만 설치된 `windowdock.desktop`을 `~/.config/autostart/`에 복사하세요. 설치만으로 자동 실행을 등록하지 않습니다. 자동 실행 항목은 앱 제거 시 별도로 제거해야 합니다.

## 검증 명령

저장소 루트에서:

```bash
PYTHONPATH=linux /usr/bin/python3 -m unittest discover -s linux/tests -p 'test_*.py' -v
mkdir -p artifacts/checks
PYTHONPATH=linux /usr/bin/python3 linux/tests/native_smoke.py \
  --output artifacts/checks/linux-native.json \
  --screenshot artifacts/checks/linux-ui.png
/usr/bin/python3 scripts/package_linux.py
```

첫 검사는 정상 매칭·중복 후보·제목 변경·재실행·음수 모니터 좌표·깨진 파일·잘못된 입력을 검사합니다. 두 번째는 실제 X11 창 관리자 아래 별도 시험 창을 생성해서 이동·크기·최대화·최소화·재열기·UI 버튼 동작을 확인합니다. 개인 창을 이동하거나 개인 배치에 저장하지 않습니다. UI 검사는 프로그램이 버튼 신호를 발생시키는 통합 검사이며, 사람의 마우스 조작이나 재부팅 검증을 뜻하지 않습니다.

Linux 배포 파일은 Python 소스와 실행기입니다. x64 Ubuntu에서 검증했으며 Python·GTK 라이브러리를 포함하는 독립 바이너리는 아닙니다. CI는 Ubuntu 24.04의 Xvfb/Openbox와 Windows 실행 파일 빌드·자체 검사를 수행합니다.
