import type { CSSProperties, ReactNode } from "react";
import {
  AbsoluteFill,
  Easing,
  interpolate,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";

export const mint = "#92f2cb";
export const ease = Easing.bezier(0.22, 1, 0.36, 1);
export const clamp = {
  extrapolateLeft: "clamp",
  extrapolateRight: "clamp",
  easing: ease,
} as const;

export const positions = {
  browser: { left: 20, top: 20, width: 350, height: 310 },
  editor: { left: 386, top: 20, width: 330, height: 186 },
  chat: { left: 386, top: 222, width: 330, height: 108 },
};

export const Frame = ({
  step,
  title,
  subtitle,
  footer,
  children,
  panel,
}: {
  step: number;
  title: string;
  subtitle: string;
  footer: string;
  children: ReactNode;
  panel: ReactNode;
}) => {
  const frame = useCurrentFrame();
  const { fps, durationInFrames } = useVideoConfig();
  return (
    <AbsoluteFill
      style={{
        background: "#101c2a",
        color: "#f4f7fa",
        fontFamily: '"Malgun Gothic", "Noto Sans KR", sans-serif',
      }}
    >
      <div
        style={{
          position: "absolute",
          left: 44,
          top: 28,
          display: "flex",
          alignItems: "center",
          gap: 13,
        }}
      >
        <svg width="32" height="32" viewBox="0 0 32 32">
          <rect width="32" height="32" rx="8" fill={mint} />
          <rect x="6" y="7" width="9" height="18" rx="2" fill="#101c2a" />
          <rect x="18" y="7" width="8" height="8" rx="2" fill="#101c2a" />
          <rect x="18" y="18" width="8" height="7" rx="2" fill="#101c2a" />
        </svg>
        <span style={{ fontSize: 25, fontWeight: 800 }}>
          창자리{" "}
          <span style={{ color: "#a0b2c7", fontSize: 19, fontWeight: 500 }}>
            WindowDock
          </span>
        </span>
      </div>
      <div
        style={{
          position: "absolute",
          right: 44,
          top: 35,
          color: "#acbbce",
          fontSize: 17,
        }}
      >
        동작 설명 · 예시 화면
      </div>
      <div
        style={{
          position: "absolute",
          left: 44,
          top: 91,
          display: "flex",
          alignItems: "center",
          gap: 18,
        }}
      >
        <span
          style={{
            background: mint,
            color: "#102537",
            fontSize: 22,
            fontWeight: 800,
            padding: "8px 12px",
            borderRadius: 8,
          }}
        >
          {String(step).padStart(2, "0")}
        </span>
        <span style={{ fontSize: 43, fontWeight: 800, letterSpacing: -1.7 }}>
          {title}
        </span>
      </div>
      <div
        style={{
          position: "absolute",
          left: 44,
          top: 160,
          color: "#bfcdde",
          fontSize: 23,
        }}
      >
        {subtitle}
      </div>
      <div
        style={{
          position: "absolute",
          left: 44,
          top: 216,
          width: 740,
          height: 386,
          borderRadius: 15,
          background: "#213348",
          border: "2px solid #3b5066",
          overflow: "hidden",
        }}
      >
        <div style={{ position: "absolute", inset: 0 }}>{children}</div>
        <div
          style={{
            position: "absolute",
            left: 0,
            bottom: 0,
            right: 0,
            height: 36,
            background: "#18283a",
            display: "flex",
            justifyContent: "center",
            alignItems: "center",
            gap: 10,
          }}
        >
          <div
            style={{
              background: "#b8d6ff",
              width: 17,
              height: 17,
              borderRadius: 4,
            }}
          />
          <div
            style={{
              background: "#d6c0ff",
              width: 17,
              height: 17,
              borderRadius: 4,
            }}
          />
          <div
            style={{ background: mint, width: 17, height: 17, borderRadius: 4 }}
          />
          <span
            style={{
              position: "absolute",
              right: 12,
              color: "#a1b3c8",
              fontSize: 12,
            }}
          >
            Windows · 예시
          </span>
        </div>
      </div>
      <div
        style={{
          position: "absolute",
          left: 808,
          top: 216,
          width: 268,
          height: 390,
        }}
      >
        {panel}
      </div>
      <div
        style={{
          position: "absolute",
          left: 44,
          right: 44,
          top: 627,
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
        }}
      >
        <span style={{ color: mint, fontSize: 22, fontWeight: 600 }}>
          {footer}
        </span>
        <span style={{ color: "#a0b2c7", fontSize: 16 }}>{step} / 4</span>
      </div>
      <div
        style={{
          position: "absolute",
          left: 44,
          right: 44,
          bottom: 20,
          height: 3,
          background: "#304154",
          borderRadius: 3,
        }}
      >
        <div
          style={{
            height: "100%",
            background: mint,
            width: `${interpolate(frame, [0, Math.min(durationInFrames, (step > 2 ? 6 : 5) * fps)], [0, 100], { extrapolateRight: "clamp" })}%`,
          }}
        />
      </div>
    </AbsoluteFill>
  );
};

export const AppWindow = ({
  kind,
  style,
}: {
  kind: "browser" | "editor" | "chat";
  style?: CSSProperties;
}) => {
  const color = { browser: "#b8d6ff", editor: "#d6c0ff", chat: mint }[kind];
  const label = {
    browser: "브라우저 · 자료",
    editor: "편집기 · 작업",
    chat: "메신저",
  }[kind];
  return (
    <div
      style={{
        position: "absolute",
        borderRadius: 9,
        background: "#f3f6fa",
        border: `2px solid ${color}`,
        overflow: "hidden",
        boxShadow: "0 6px 12px #08142430",
        ...style,
      }}
    >
      <div
        style={{
          height: 32,
          background: color,
          color: "#20364d",
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          padding: "0 10px",
          fontSize: 14,
          fontWeight: 700,
        }}
      >
        <span>{label}</span>
        <span style={{ letterSpacing: 7, fontWeight: 400 }}>− □ ×</span>
      </div>
      {kind === "browser" && (
        <div style={{ padding: 15 }}>
          <div
            style={{
              height: 20,
              background: "#dce5f0",
              borderRadius: 5,
              marginBottom: 18,
            }}
          />
          <div style={{ display: "flex", gap: 12 }}>
            <div
              style={{
                width: "52%",
                height: 113,
                background: "#c5d9f0",
                borderRadius: 7,
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
              }}
            >
              <svg width="70" height="60" viewBox="0 0 70 60">
                <path d="M5 50 L23 29 L36 40 L51 19 L65 50 Z" fill="#7eacd7" />
                <circle cx="19" cy="15" r="7" fill="#f8fcff" />
              </svg>
            </div>
            <div style={{ flex: 1 }}>
              {[94, 65, 81, 49].map((width, i) => (
                <div
                  key={i}
                  style={{
                    width: `${width}%`,
                    height: 8,
                    background: "#b5c6d9",
                    borderRadius: 3,
                    marginBottom: 12,
                  }}
                />
              ))}
            </div>
          </div>
          <div
            style={{
              color: "#435a70",
              fontSize: 18,
              fontWeight: 700,
              marginTop: 18,
            }}
          >
            오늘의 자료 모음
          </div>
          <div
            style={{
              height: 8,
              width: "75%",
              background: "#d8e2ed",
              borderRadius: 3,
              marginTop: 12,
            }}
          />
        </div>
      )}
      {kind === "editor" && (
        <div style={{ padding: "14px 15px" }}>
          {[65, 88, 43, 75, 54].map((width, i) => (
            <div
              key={i}
              style={{
                display: "flex",
                gap: 12,
                alignItems: "center",
                marginBottom: 9,
              }}
            >
              <span style={{ color: "#8c91a9", fontSize: 12 }}>{i + 1}</span>
              <div
                style={{
                  height: 7,
                  width: `${width}%`,
                  background: i % 2 ? "#b8b0d7" : "#cbd6e5",
                  borderRadius: 3,
                }}
              />
            </div>
          ))}
        </div>
      )}
      {kind === "chat" && (
        <div
          style={{
            display: "flex",
            gap: 10,
            padding: "12px 15px",
            alignItems: "center",
          }}
        >
          <div
            style={{
              background: "#c5eadd",
              height: 26,
              width: 26,
              borderRadius: 20,
            }}
          />
          <div
            style={{
              background: "#e0ebe7",
              borderRadius: 6,
              padding: "8px 12px",
              fontSize: 13,
              color: "#436258",
            }}
          >
            여기서 이어서 작업해요
          </div>
        </div>
      )}
    </div>
  );
};

export const Slot = () => (
  <div
    style={{
      position: "absolute",
      ...positions.chat,
      boxSizing: "border-box",
      border: "2px dashed #71849a",
      borderRadius: 9,
      display: "flex",
      alignItems: "center",
      justifyContent: "center",
      color: "#b1c1d0",
      fontSize: 17,
    }}
  >
    메신저의 저장된 자리
  </div>
);

export const Panel = ({
  saved = true,
  watching = false,
  highlight = false,
  button,
  note,
}: {
  saved?: boolean;
  watching?: boolean;
  highlight?: boolean;
  button: string;
  note: string;
}) => (
  <div
    style={{
      border: "1px solid #43576e",
      borderRadius: 13,
      height: "100%",
      boxSizing: "border-box",
      padding: "21px 17px",
      background: "#1b2b3f",
    }}
  >
    <div style={{ color: "#bacbdd", fontSize: 15, marginBottom: 15 }}>
      창자리 · 저장된 배치
    </div>
    <div
      style={{
        border: `1px solid ${saved ? "#92f2cb" : "#54667b"}`,
        background: saved ? "#243f46" : "#223246",
        borderRadius: 8,
        padding: "18px 12px",
        minHeight: 41,
      }}
    >
      <div
        style={{
          color: saved ? mint : "#b7c5d6",
          fontSize: 22,
          fontWeight: 700,
        }}
      >
        {saved ? "집중 작업" : "아직 배치가 없어요"}
      </div>
      <div style={{ color: "#c4d4e0", fontSize: 14, marginTop: 6 }}>
        {saved ? "브라우저 · 편집기 · 메신저" : "내 창으로 처음 등록하기"}
      </div>
    </div>
    <div
      style={{
        display: "flex",
        alignItems: "center",
        gap: 8,
        margin: "19px 0",
        fontSize: 17,
        color: watching ? mint : "#bdcbd9",
      }}
    >
      <span
        style={{
          width: 8,
          height: 8,
          background: watching ? mint : "#91a2b7",
          borderRadius: 8,
        }}
      />
      자동 배치 {watching ? "켜짐" : "꺼짐"}
    </div>
    <div
      style={{
        padding: "15px 4px",
        textAlign: "center",
        border: `2px solid ${highlight ? "#e9fff7" : mint}`,
        borderRadius: 8,
        background: highlight ? "#c0ffe6" : mint,
        color: "#102a32",
        fontSize: 15,
        fontWeight: 800,
        boxShadow: highlight ? "0 0 0 5px #92f2cb25" : "none",
      }}
    >
      {button}
    </div>
    <div
      style={{
        marginTop: 21,
        color: "#c0ccda",
        fontSize: 17,
        lineHeight: 1.7,
        whiteSpace: "pre-line",
      }}
    >
      {note}
    </div>
  </div>
);

export const Pointer = ({
  x,
  y,
  click = false,
}: {
  x: number;
  y: number;
  click?: boolean;
}) => (
  <div
    style={{
      position: "absolute",
      left: x,
      top: y,
      width: 32,
      height: 42,
      zIndex: 20,
    }}
  >
    {click && (
      <div
        style={{
          position: "absolute",
          left: -14,
          top: -14,
          width: 36,
          height: 36,
          border: `3px solid ${mint}`,
          borderRadius: "50%",
          background: "#92f2cb22",
        }}
      />
    )}
    <svg width="28" height="36" viewBox="0 0 28 36">
      <path
        d="M3 2 L3 28 L10 22 L16 34 L22 31 L16 20 L26 20 Z"
        fill="#fff"
        stroke="#172639"
        strokeWidth="2.5"
        strokeLinejoin="round"
      />
    </svg>
  </div>
);
