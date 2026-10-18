import { interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import {
  AppWindow,
  clamp,
  Frame,
  mint,
  Panel,
  Pointer,
  positions,
  Slot,
} from "./Visuals";

export const KeepScene = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const closed = frame >= 3.6 * fps;
  const x = interpolate(
    frame,
    [0.8 * fps, 1.9 * fps],
    [positions.browser.left, 62],
    clamp,
  );
  const y = interpolate(
    frame,
    [0.8 * fps, 1.9 * fps],
    [positions.browser.top, 39],
    clamp,
  );
  return (
    <Frame
      step={4}
      title="내가 옮긴 창은 그대로"
      subtitle="수동 이동은 존중하고, 창을 닫으면 빈자리도 유지"
      footer="내 PC에만 저장 · 버튼으로 복원 · 트레이에서 감시"
      panel={
        <Panel
          watching
          button="자동 배치 일시정지"
          note={
            closed
              ? "빈자리 유지\n다른 창을 늘리지 않아요."
              : "직접 옮긴 위치 유지\n자동으로 되돌리지 않아요."
          }
        />
      }
    >
      {closed && <Slot />}
      <AppWindow kind="editor" style={positions.editor} />
      {!closed && <AppWindow kind="chat" style={positions.chat} />}
      <AppWindow
        kind="browser"
        style={{ ...positions.browser, left: x, top: y }}
      />
      {frame >= 0.5 * fps && frame < 2.4 * fps && (
        <Pointer x={x + 135} y={y + 16} click />
      )}
      {frame >= 2.4 * fps && frame < 3.1 * fps && (
        <div
          style={{
            position: "absolute",
            left: 111,
            top: 166,
            background: mint,
            padding: "10px 17px",
            borderRadius: 20,
            color: "#173d38",
            fontSize: 17,
            fontWeight: 700,
          }}
        >
          옮긴 위치 그대로
        </div>
      )}
      {frame >= 3.1 * fps && frame < 4.0 * fps && (
        <Pointer x={699} y={236} click={frame >= 3.5 * fps} />
      )}
    </Frame>
  );
};
