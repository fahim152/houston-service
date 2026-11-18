import type { CSSProperties } from "react";
import { fmt } from "../diff";
import type { Rocket, RocketChange } from "../types";

interface Props {
  rocket: Rocket;
  changes: RocketChange[]; // newest first
}

const ICONS: Record<RocketChange["kind"], string> = {
  launch: "🚀",
  speedUp: "⏫",
  slowDown: "⏬",
  mission: "🛰️",
  explosion: "💥",
};

// Spread of the explosion debris: fixed, so it looks the same every time.
const DEBRIS = Array.from({ length: 18 }, (_, i) => {
  const angle = (i / 18) * Math.PI * 2 + (i % 3) * 0.2;
  const distance = 120 + (i % 5) * 35;
  return { dx: Math.cos(angle) * distance, dy: Math.sin(angle) * distance, rotate: (i * 67) % 360, size: 6 + (i % 4) * 4 };
});

export function RocketStage({ rocket, changes }: Props) {
  const exploded = rocket.status === "Exploded";
  const reverse = rocket.speed < 0;

  // 0 … 1 on a log scale, so 500 and 100,000 both look sensible.
  const factor = Math.min(1, Math.log10(Math.abs(rocket.speed) + 1) / 5);
  const starSeconds = 14 - 12 * factor;
  const flameScale = 0.35 + 1.1 * factor;

  // Changes from the most recent poll (they share a timestamp) drive the one-off animations, but only while they're fresh,
  // so selecting a rocket doesn't replay something that happened minutes ago.
  const fresh = changes[0] && Date.now() - changes[0].at.getTime() < 7000;
  const latest = fresh ? changes.filter((c) => c.at === changes[0].at) : [];
  const speedChange = latest.find((c) => c.kind === "speedUp" || c.kind === "slowDown");
  const missionChange = latest.find((c) => c.kind === "mission");
  const justExploded = latest.find((c) => c.kind === "explosion");

  return (
    <section className="stage">
      <div className="stage-header">
        <div>
          <h1>{rocket.type ?? "Unknown rocket"}</h1>
          <div className="channel">{rocket.channel}</div>
        </div>
        <span className={`badge big ${exploded ? "bad" : "ok"}`}>{exploded ? "💥 Exploded" : "🔥 Launched"}</span>
      </div>

      <div
        className={`sky ${exploded ? "frozen" : ""} ${reverse ? "reverse" : ""} ${justExploded ? "shake" : ""}`}
        style={{ "--star-seconds": `${starSeconds}s` } as CSSProperties}
        key={`sky-${justExploded?.id ?? "calm"}`}
      >
        <div className="stars layer1" />
        <div className="stars layer2" />

        {missionChange && (
          <div className="toast" key={`toast-${missionChange.id}`}>
            🛰️ New mission: <b>{rocket.mission}</b>
          </div>
        )}

        {speedChange && !exploded && (
          <div className={`delta ${speedChange.kind === "speedUp" ? "up" : "down"}`} key={`delta-${speedChange.id}`}>
            {speedChange.kind === "speedUp" ? "+" : "−"}
            {fmt(Math.abs(speedChange.delta ?? 0))}
          </div>
        )}

        {!exploded && (
          <div
            className={`rocket-wrap ${speedChange?.kind === "speedUp" ? "boost" : speedChange?.kind === "slowDown" ? "brake" : ""}`}
            key={`rocket-${speedChange?.id ?? "idle"}`}
          >
            <div className="bob">
              <RocketSvg label={rocket.type ?? ""} />
              <div className="flame" style={{ "--flame": flameScale } as CSSProperties}>
                <svg viewBox="0 0 40 90" width="40" height="90">
                  <path className="flame-outer" d="M20 0 C34 22 36 50 20 90 C4 50 6 22 20 0 Z" />
                  <path className="flame-inner" d="M20 6 C28 24 29 46 20 70 C11 46 12 24 20 6 Z" />
                </svg>
              </div>
              {speedChange?.kind === "slowDown" && (
                <>
                  <span className="retro left" />
                  <span className="retro right" />
                </>
              )}
            </div>
          </div>
        )}

        {exploded && (
          <>
            {justExploded && (
              <div className="explosion" key={`boom-${justExploded.id}`}>
                <div className="flash" />
                <div className="ring" />
                {DEBRIS.map((d, i) => (
                  <span
                    key={i}
                    className="debris"
                    style={{ "--dx": `${d.dx}px`, "--dy": `${d.dy}px`, "--rot": `${d.rotate}deg`, width: d.size, height: d.size } as CSSProperties}
                  />
                ))}
              </div>
            )}
            <div className={`wreck ${justExploded ? "delayed" : ""}`}>
              <RocketSvg label={rocket.type ?? ""} />
              <span className="fire f1" />
              <span className="fire f2" />
              <span className="smoke s1" />
              <span className="smoke s2" />
              <span className="smoke s3" />
            </div>
            <div className="explosion-reason">💥 {rocket.explosionReason ?? "Exploded"}</div>
          </>
        )}

        <div className="speed-readout">
          <div className="speed-value">{fmt(rocket.speed)}</div>
          <div className="speed-label">{reverse ? "speed (negative)" : "speed"}</div>
          <div className="gauge"><div className="gauge-fill" style={{ width: `${factor * 100}%` }} /></div>
        </div>
      </div>

      <div className="cards">
        <Card label="Mission" value={rocket.mission ?? "—"} />
        <Card label="Status" value={rocket.status} tone={exploded ? "bad" : "ok"} />
        <Card label="Last message #" value={fmt(rocket.lastMessageNumber)} />
        <Card label="Waiting messages" value={String(rocket.pendingMessages)} tone={rocket.pendingMessages > 0 ? "warn" : undefined} />
        <Card label="Launched at" value={time(rocket.launchedAt)} />
        <Card label="Last update" value={time(rocket.lastUpdatedAt)} />
      </div>

      <div className="events">
        <h2>What changed <span className="muted">(net change between polls · event time · message numbers)</span></h2>
        {changes.length === 0 ? (
          <p className="muted">Nothing yet. Changes appear here as the rocket receives messages.</p>
        ) : (
          <ul>
            {changes.map((c) => (
              <li key={c.id} className={`ev-${c.kind}`}>
                <span className="event-time">{(c.happenedAt ?? c.at).toLocaleTimeString()}</span>
                <span className="event-range">
                  #{c.fromNumber >= c.toNumber ? fmt(c.toNumber) : `${fmt(c.fromNumber)}–${fmt(c.toNumber)}`}
                </span>
                <span className="event-icon">{ICONS[c.kind]}</span>
                <span>{c.text}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

function Card({ label, value, tone }: { label: string; value: string; tone?: "ok" | "bad" | "warn" }) {
  return (
    <div className={`card ${tone ?? ""}`}>
      <div className="card-label">{label}</div>
      <div className="card-value">{value}</div>
    </div>
  );
}

function time(value: string | null) {
  return value ? new Date(value).toLocaleString() : "—";
}

function RocketSvg({ label }: { label: string }) {
  return (
    <svg className="rocket-svg" viewBox="0 0 100 170" width="110" height="187">
      <path d="M30 120 L8 160 L32 148 Z" className="fin" />
      <path d="M70 120 L92 160 L68 148 Z" className="fin" />
      <path d="M50 4 C72 34 74 90 70 150 L30 150 C26 90 28 34 50 4 Z" className="body" />
      <path d="M50 4 C61 18 66 32 67 44 L33 44 C34 32 39 18 50 4 Z" className="nose" />
      <circle cx="50" cy="72" r="12" className="window" />
      <circle cx="46" cy="68" r="4" className="window-shine" />
      <text x="50" y="119" className="body-text" transform="rotate(-90 50 119)">{label.slice(0, 12)}</text>
      <rect x="38" y="150" width="24" height="9" rx="2" className="nozzle" />
    </svg>
  );
}
