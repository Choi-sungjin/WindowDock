import { Composition, Folder } from "remotion";
import { WindowDockDemo } from "./Composition";
import { SaveScene } from "./SaveScene";
import { ApplyScene } from "./ApplyScene";
import { WatchScene } from "./WatchScene";
import { KeepScene } from "./KeepScene";

export const RemotionRoot = () => (
  <>
    <Composition
      id="WindowDock"
      component={WindowDockDemo}
      durationInFrames={660}
      fps={30}
      width={1120}
      height={700}
    />
    <Folder name="Scenes">
      <Composition
        id="Save"
        component={SaveScene}
        durationInFrames={150}
        fps={30}
        width={1120}
        height={700}
      />
      <Composition
        id="Apply"
        component={ApplyScene}
        durationInFrames={150}
        fps={30}
        width={1120}
        height={700}
      />
      <Composition
        id="Watch"
        component={WatchScene}
        durationInFrames={180}
        fps={30}
        width={1120}
        height={700}
      />
      <Composition
        id="Keep"
        component={KeepScene}
        durationInFrames={180}
        fps={30}
        width={1120}
        height={700}
      />
    </Folder>
  </>
);
