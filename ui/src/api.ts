import type { Rocket, SortField } from "./types";

/** GET /rockets through the Vite proxy (/api → the Rockets service). */
export async function fetchRockets(sortBy: SortField, order: "asc" | "desc"): Promise<Rocket[]> {
  const response = await fetch(`/api/rockets?sortBy=${sortBy}&order=${order}`);
  if (!response.ok) {
    throw new Error(`GET /rockets → ${response.status}`);
  }
  return response.json();
}
