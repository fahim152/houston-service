import { useCallback, useEffect, useRef, useState } from "react";
import { fetchRockets } from "./api";
import { diffRocket } from "./diff";
import type { Rocket, RocketChange, SortField } from "./types";
import { RocketList } from "./components/RocketList";
import { RocketStage } from "./components/RocketStage";

const POLL_MS = 5000;
const MAX_CHANGES_PER_ROCKET = 60;

export default function App() {
  const [rockets, setRockets] = useState<Rocket[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [changes, setChanges] = useState<Record<string, RocketChange[]>>({});
  const [error, setError] = useState<string | null>(null);
  const [lastPoll, setLastPoll] = useState<Date | null>(null);
  const [nextPollAt, setNextPollAt] = useState(Date.now() + POLL_MS);
  const [now, setNow] = useState(Date.now());
  const [sortBy, setSortBy] = useState<SortField>("channel");
  const [order, setOrder] = useState<"asc" | "desc">("asc");

  // The previous poll, to work out what changed. A ref, so comparing doesn't cause re-renders.
  const previous = useRef<Map<string, Rocket> | null>(null);

  const poll = useCallback(async () => {
    try {
      const latest = await fetchRockets(sortBy, order);
      const before = previous.current;
      if (before) {
        const found: Record<string, RocketChange[]> = {};
        for (const rocket of latest) {
          const diff = diffRocket(before.get(rocket.channel), rocket);
          if (diff.length) found[rocket.channel] = diff;
        }
        if (Object.keys(found).length) {
          setChanges((all) => {
            const next = { ...all };
            for (const [channel, list] of Object.entries(found)) {
              // Newest event first, by message number (a stable sort keeps the order within one poll).
              next[channel] = [...list, ...(next[channel] ?? [])]
                .sort((a, b) => b.toNumber - a.toNumber)
                .slice(0, MAX_CHANGES_PER_ROCKET);
            }
            return next;
          });
        }
      }
      previous.current = new Map(latest.map((r) => [r.channel, r]));
      setRockets(latest);
      setError(null);
      setLastPoll(new Date());
      setSelected((current) => current ?? latest[0]?.channel ?? null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setNextPollAt(Date.now() + POLL_MS);
    }
  }, [sortBy, order]);

  // Poll now, then every POLL_MS. Changing the sort restarts the cycle with an immediate poll.
  useEffect(() => {
    void poll();
    const timer = setInterval(() => void poll(), POLL_MS);
    return () => clearInterval(timer);
  }, [poll]);

  // A 1-second tick, only for the "next refresh in …" countdown.
  useEffect(() => {
    const tick = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(tick);
  }, []);

  const rocket = rockets.find((r) => r.channel === selected) ?? null;
  const secondsLeft = Math.min(POLL_MS / 1000, Math.max(0, Math.round((nextPollAt - now) / 1000)));

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">🚀 Rockets <span className="muted">mission control</span></div>
        <div className={`status ${error ? "down" : "up"}`}>
          <span className="dot" />
          {error
            ? `Can't reach the Rockets service (${error}). Is it running on localhost:8088?`
            : lastPoll
              ? `${rockets.length} rockets · updated ${lastPoll.toLocaleTimeString()} · next in ${secondsLeft}s`
              : "connecting…"}
        </div>
      </header>

      <main className="layout">
        <RocketList
          rockets={rockets}
          selected={selected}
          onSelect={setSelected}
          lastChanges={changes}
          sortBy={sortBy}
          order={order}
          onSort={(field, dir) => {
            setSortBy(field);
            setOrder(dir);
          }}
        />
        {rocket ? (
          <RocketStage rocket={rocket} changes={changes[rocket.channel] ?? []} />
        ) : (
          <section className="stage empty">
            <p>No rockets yet.</p>
          </section>
        )}
      </main>
    </div>
  );
}
