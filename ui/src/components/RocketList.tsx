import { useMemo, useState } from "react";
import { fmt } from "../diff";
import type { Rocket, RocketChange, SortField } from "../types";

interface Props {
  rockets: Rocket[];
  selected: string | null;
  onSelect: (channel: string) => void;
  lastChanges: Record<string, RocketChange[]>;
  sortBy: SortField;
  order: "asc" | "desc";
  onSort: (field: SortField, order: "asc" | "desc") => void;
}

const SORTS: { label: string; field: SortField; order: "asc" | "desc" }[] = [
  { label: "Channel", field: "channel", order: "asc" },
  { label: "Fastest", field: "speed", order: "desc" },
  { label: "Slowest", field: "speed", order: "asc" },
  { label: "Type", field: "type", order: "asc" },
  { label: "Status", field: "status", order: "asc" },
  { label: "Newest launch", field: "launchedAt", order: "desc" },
];

export function RocketList({ rockets, selected, onSelect, lastChanges, sortBy, order, onSort }: Props) {
  const [filter, setFilter] = useState("");

  const visible = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return f
      ? rockets.filter((r) => [r.channel, r.type, r.mission].some((v) => v?.toLowerCase().includes(f)))
      : rockets;
  }, [rockets, filter]);

  return (
    <aside className="sidebar">
      <div className="sidebar-tools">
        <input placeholder="Filter by channel, type or mission…" value={filter} onChange={(e) => setFilter(e.target.value)} />
        <select
          value={`${sortBy}:${order}`}
          onChange={(e) => {
            const [field, dir] = e.target.value.split(":");
            onSort(field as SortField, dir as "asc" | "desc");
          }}
        >
          {SORTS.map((s) => (
            <option key={`${s.field}:${s.order}`} value={`${s.field}:${s.order}`}>
              Sort: {s.label}
            </option>
          ))}
        </select>
      </div>

      <ul className="rocket-list">
        {visible.map((r) => {
          const last = lastChanges[r.channel]?.find((c) => c.kind === "speedUp" || c.kind === "slowDown");
          const trend = last?.kind === "speedUp" ? "up" : last?.kind === "slowDown" ? "down" : "";
          return (
            <li key={r.channel} className={`rocket-item ${r.channel === selected ? "active" : ""} ${r.status.toLowerCase()}`} onClick={() => onSelect(r.channel)}>
              <span className="item-icon">{r.status === "Exploded" ? "💥" : "🚀"}</span>
              <span className="item-main">
                <span className="item-title">{r.type ?? "Unknown"}</span>
                <span className="item-channel" title={r.channel}>{r.channel}</span>
              </span>
              <span className="item-side">
                <span className={`item-speed ${trend}`}>
                  {trend === "up" ? "▲ " : trend === "down" ? "▼ " : ""}
                  {fmt(r.speed)}
                </span>
                <span className="item-mission">{r.mission}</span>
                {r.pendingMessages > 0 && <span className="badge warn">{r.pendingMessages} waiting</span>}
              </span>
            </li>
          );
        })}
        {visible.length === 0 && <li className="muted list-empty">No rockets match.</li>}
      </ul>
    </aside>
  );
}
