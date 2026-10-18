import { interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import {
  AppWindow,
  clamp,
  Frame,
  mint,
  Panel,
  positions,
  Slot,
} from "./Visuals";

export const WatchScene = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const opened = frame >= 1.5 * fps;
  return (
    <Frame
      step={3}
      title="나중에 연 창도, 자기 자리로"
      subtitle="자동 배치가 켜져 있으면 새로 열린 일치 창을 한 번 배치"
      footer="꺼진 프로그램을 창자리가 대신 실행하지는 않아요."
      panel={
        <Panel
          watching
          button="자동 배치 일시정지"
          note={
            !opened
              ? "메신저가 닫혀 있어도\n그 자리를 남겨 둡니다."
              : frame < 3.2 * fps
                ? "사용자가 메신저 실행\n새 창을 감지했어요."
                : "새로 열린 메신저만\n저장된 자리로 이동!"
          }
        />
      }
    >
      <Slot />
      <AppWindow kind="browser" style={positions.browser} />
      <AppWindow kind="editor" style={positions.editor} />
      {opened && (
        <AppWindow
          kind="chat"
          style={{
            left: interpolate(
              frame,
              [2.3 * fps, 3.5 * fps],
              [180, positions.chat.left],
              clamp,
            ),
            top: interpolate(
              frame,
              [2.3 * fps, 3.5 * fps],
              [130, positions.chat.top],
              clamp,
            ),
            width: positions.chat.width,
            height: positions.chat.height,
            opacity: interpolate(frame, [1.5 * fps, 1.8 * fps], [0, 1], clamp),
          }}
        />
      )}
      {frame >= 1.1 * fps && frame < 2.6 * fps && (
        <div
          style={{
            position: "absolute",
            left: 205,
            bottom: 48,
            padding: "10px 17px",
            borderRadius: 20,
            background: mint,
            color: "#173d38",
            fontSize: 16,
            fontWeight: 700,
          }}
        >
          사용자가 메신저를 열었어요
        </div>
      )}
    </Frame>
  );
};
