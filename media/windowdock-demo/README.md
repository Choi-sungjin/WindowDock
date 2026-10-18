# README 설명 애니메이션

창자리의 네 가지 동작을 보여 주는 **22초 반복 GIF**입니다. 모든 창, 제목, 배치 이름은 설명을 위해 만든 가상 예시입니다. 실제 앱 화면 녹화나 사용자의 저장 배치를 사용하지 않습니다. 이 프로젝트는 앱 실행에 필요하지 않습니다.

- 0–5초: 원하는 창 배치를 이름으로 저장
- 5–10초: 버튼으로 열린 창을 저장 위치로 복원
- 10–16초: 사용자가 나중에 연 일치 창을 한 번 자동 배치
- 16–22초: 수동 이동과 닫힌 창의 빈자리 유지

## 미리보기와 재생성

이 폴더에서 실행합니다. Node.js 20.19 이상 또는 22.13 이상이 필요합니다. 한글은 Windows의 맑은 고딕을 사용하며, 다른 OS에서는 Noto Sans KR 등 한글 글꼴을 설치하세요.

```powershell
npm ci
npm run dev
```

Studio에서 `WindowDock`을 선택하세요. `Scenes` 폴더에서 각 장면도 따로 볼 수 있습니다.

```powershell
npm run lint
npm run render:gif
```

출력은 `../../docs/images/windowdock-demo.gif`입니다. 1120×700, 15fps, 무한 반복이며 Remotion 4.0.536으로 렌더링합니다. 원본 타임라인은 30fps이고, GIF는 두 프레임마다 한 장씩 출력합니다. 명령은 기존 GIF를 갱신합니다. 첫 렌더링에는 Chrome Headless Shell이 자동으로 다운로드될 수 있습니다.

[Remotion GIF 렌더링 문서](https://www.remotion.dev/docs/render-as-gif) · [Remotion 라이선스](https://www.remotion.dev/docs/license)

## 파일

- `src/Composition.tsx`: 전체 타임라인
- `src/SaveScene.tsx`, `ApplyScene.tsx`, `WatchScene.tsx`, `KeepScene.tsx`: 네 장면
- `src/Visuals.tsx`: 예시 창과 공통 화면
- `src/Root.tsx`: 전체 영상과 개별 장면 등록

실제 창을 열거나 이동하는 코드는 없습니다. 사용자 AppData, 창 제목, 개인 실행 경로를 읽지 않습니다. `node_modules`와 중간 렌더링 파일은 Git에서 제외합니다.
