import {
  AbsoluteFill,
  interpolate,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { AppWindow, clamp, Frame, Panel, Pointer, positions } from "./Visuals";

export const ApplyScene = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const applied = frame >= 1.4 * fps;
  return (
    <AbsoluteFill>
      <Frame
        step={2}
        title="흩어진 창도, 버튼 한 번으로"
        subtitle="저장한 배치를 선택하고, 지금 적용 + 자동 배치 시작"
        footer="현재 열려 있고 조건에 맞는 창만 이동합니다."
        panel={
          <Panel
            watching={applied}
            highlight={frame >= 1.4 * fps && frame < 1.9 * fps}
            button="지금 적용 + 자동 배치 시작"
            note={
              applied
                ? "저장된 위치로 복원\n새 창 감시도 시작해요."
                : "저장한 ‘집중 작업’을\n다시 불러옵니다."
            }
          />
        }
      >
        <AppWindow
          kind="browser"
          style={{
            left: interpolate(
              frame,
              [1.5 * fps, 2.7 * fps],
              [215, positions.browser.left],
              clamp,
            ),
            top: interpolate(
              frame,
              [1.5 * fps, 2.7 * fps],
              [42, positions.browser.top],
              clamp,
            ),
            width: interpolate(
              frame,
              [1.5 * fps, 2.7 * fps],
              [410, positions.browser.width],
              clamp,
            ),
            height: interpolate(
              frame,
              [1.5 * fps, 2.7 * fps],
              [280, positions.browser.height],
              clamp,
            ),
          }}
        />
        <AppWindow
          kind="editor"
          style={{
            left: interpolate(
              frame,
              [1.5 * fps, 2.7 * fps],
              [40, positions.editor.left],
              clamp,
            ),
            top: interpolate(
              frame,
              [1.5 * fps, 2.7 * fps],
              [92, positions.editor.top],
              clamp,
            ),
            width: positions.editor.width,
            height: positions.editor.height,
          }}
        />
        <AppWindow
          kind="chat"
          style={{
            left: interpolate(
              frame,
              [1.5 * fps, 2.7 * fps],
              [190, positions.chat.left],
              clamp,
            ),
            top: interpolate(
              frame,
              [1.5 * fps, 2.7 * fps],
              [216, positions.chat.top],
              clamp,
            ),
            width: positions.chat.width,
            height: positions.chat.height,
          }}
        />
      </Frame>
      {frame >= 0.8 * fps && frame < 2.3 * fps && (
        <Pointer
          x={970}
          y={450}
          click={frame >= 1.4 * fps && frame < 1.9 * fps}
        />
      )}
    </AbsoluteFill>
  );
};
