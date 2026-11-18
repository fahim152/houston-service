export type RocketStatus = "AwaitingLaunch" | "Launched" | "Exploded";

/** One rocket, exactly as GET /rockets returns it. */
export interface Rocket {
  channel: string;
  type: string | null;
  speed: number;
  mission: string | null;
  status: RocketStatus;
  explosionReason: string | null;
  launchedAt: string | null;
  lastUpdatedAt: string | null;
  lastMessageNumber: number;
  pendingMessages: number;
}

export type ChangeKind = "launch" | "speedUp" | "slowDown" | "mission" | "explosion";

/** Something that changed between two polls, worked out in the browser by comparing them. */
export interface RocketChange {
  id: number;
  /** When the browser noticed it (used only to decide whether an animation is still fresh). */
  at: Date;
  /** When it happened: the rocket's lastUpdatedAt (or launchedAt), i.e. the event's own messageTime. */
  happenedAt: Date | null;
  /** The message numbers this net change covers (since the previous poll). */
  fromNumber: number;
  toNumber: number;
  kind: ChangeKind;
  text: string;
  delta?: number;
}

export type SortField = "channel" | "speed" | "type" | "mission" | "status" | "launchedAt";
