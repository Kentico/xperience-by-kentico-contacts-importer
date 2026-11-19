import { describe, expect, it } from "vitest";

import type { JobState } from "../shared/commands";
import { StateIconColors, stateIcon } from "./stateIcon";

const ALL_STATES: JobState[] = [
  "Configuring",
  "Uploading",
  "Uploaded",
  "Analyzing",
  "Analyzed",
  "Confirmed",
  "Running",
  "Paused",
  "Completed",
  "Failed",
  "Cancelled",
];

describe("stateIcon", () => {
  it("gives every state its own icon with an admin icon color", () => {
    const names = ALL_STATES.map((state) => stateIcon(state).name);
    expect(new Set(names).size).toBe(ALL_STATES.length);
    for (const state of ALL_STATES) {
      expect(Object.values(StateIconColors)).toContain(stateIcon(state).color);
    }
  });

  it("colors final states by outcome", () => {
    expect(stateIcon("Completed")).toEqual({
      name: "xp-check-circle",
      color: StateIconColors.success,
    });
    expect(stateIcon("Failed")).toEqual({
      name: "xp-times-circle",
      color: StateIconColors.alert,
    });
    expect(stateIcon("Paused")).toEqual({
      name: "xp-pause",
      color: StateIconColors.warning,
    });
    expect(stateIcon("Cancelled").color).toBe(StateIconColors.neutral);
  });

  it("falls back to a neutral question mark for unknown states", () => {
    expect(stateIcon("Mystery")).toEqual({
      name: "xp-question-circle",
      color: StateIconColors.neutral,
    });
    expect(stateIcon("toString").name).toBe("xp-question-circle");
  });
});
