import { AbsoluteFill, useCurrentFrame, useVideoConfig } from "remotion";
import { AppWindow, Frame, Panel, Pointer, positions } from "./Visuals";

export const SaveScene = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const saved = frame >= 2 * fps;
  return (
    <AbsoluteFill>
      <Frame
        step={1}
        title="원하는 배치에 이름을 붙이세요"
        subtitle="창을 원하는 곳에 놓고, 현재 창에서 새로 저장"
        footer="처음에는 빈 목록. 내 창으로 첫 배치를 등록하세요."
        panel={
          <Panel
            saved={saved}
            highlight={frame >= 1.8 * fps && frame < 2.3 * fps}
            button="현재 창에서 새로 저장"
            note={
              saved
                ? "‘집중 작업’ 저장 완료\n위치와 크기를 기억해요."
                : "포함할 창을 고르고\n배치 이름을 정하세요."
            }
          />
        }
      >
        <AppWindow kind="browser" style={positions.browser} />
        <AppWindow kind="editor" style={positions.editor} />
        <AppWindow kind="chat" style={positions.chat} />
      </Frame>
      {frame >= 1.5 * fps && frame < 2.8 * fps && (
        <Pointer
          x={966}
          y={450}
          click={frame >= 1.8 * fps && frame < 2.3 * fps}
        />
      )}
    </AbsoluteFill>
  );
};
