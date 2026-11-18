import type { Rocket, RocketChange } from "./types";

let nextId = 1;

const fmt = (n: number) => n.toLocaleString("en-US");

/** Compares a rocket between two polls and describes what changed (net change over the polling interval). */
export function diffRocket(before: Rocket | undefined, after: Rocket): RocketChange[] {
  const at = new Date();
  // Event time and message numbers come from the API (lastUpdatedAt / lastMessageNumber), not from the browser's clock.
  const happenedAt = after.lastUpdatedAt ? new Date(after.lastUpdatedAt) : null;
  const fromNumber = before ? before.lastMessageNumber + 1 : 1;
  const toNumber = after.lastMessageNumber;
  const change = (kind: RocketChange["kind"], text: string, delta?: number): RocketChange =>
    ({ id: nextId++, at, happenedAt, fromNumber, toNumber, kind, text, delta });

  if (!before) {
    return [{
      ...change("launch", `Launched: ${after.type ?? "?"} on mission ${after.mission ?? "?"} at speed ${fmt(after.speed)}`),
      happenedAt: after.launchedAt ? new Date(after.launchedAt) : happenedAt,
    }];
  }

  const changes: RocketChange[] = [];
  const delta = after.speed - before.speed;
  if (delta > 0) {
    changes.push(change("speedUp", `Speed +${fmt(delta)} (${fmt(before.speed)} → ${fmt(after.speed)})`, delta));
  } else if (delta < 0) {
    changes.push(change("slowDown", `Speed −${fmt(-delta)} (${fmt(before.speed)} → ${fmt(after.speed)})`, delta));
  }
  if (after.mission !== before.mission) {
    changes.push(change("mission", `Mission ${before.mission ?? "?"} → ${after.mission ?? "?"}`));
  }
  if (after.status === "Exploded" && before.status !== "Exploded") {
    changes.push(change("explosion", `Exploded: ${after.explosionReason ?? "unknown reason"}`));
  }
  // The list shows newest first. These changes all come from one poll, so their real order is unknown, except that an
  // explosion is always the last event (nothing changes after it): put it first, then mission, then speed.
  return changes.reverse();
}

export { fmt };
