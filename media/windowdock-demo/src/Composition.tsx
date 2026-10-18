import { Series, useVideoConfig } from "remotion";
import { SaveScene } from "./SaveScene";
import { ApplyScene } from "./ApplyScene";
import { WatchScene } from "./WatchScene";
import { KeepScene } from "./KeepScene";

export const WindowDockDemo = () => {
  const { fps } = useVideoConfig();
  return (
    <Series>
      <Series.Sequence
        name="01 원하는 배치 저장"
        durationInFrames={5 * fps}
        premountFor={fps}
      >
        <SaveScene />
      </Series.Sequence>
      <Series.Sequence
        name="02 버튼으로 복원"
        durationInFrames={5 * fps}
        premountFor={fps}
      >
        <ApplyScene />
      </Series.Sequence>
      <Series.Sequence
        name="03 새 창 자동 배치"
        durationInFrames={6 * fps}
        premountFor={fps}
      >
        <WatchScene />
      </Series.Sequence>
      <Series.Sequence
        name="04 수동 이동과 빈자리 유지"
        durationInFrames={6 * fps}
        premountFor={fps}
      >
        <KeepScene />
      </Series.Sequence>
    </Series>
  );
};
