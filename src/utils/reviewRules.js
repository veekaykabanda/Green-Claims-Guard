// these codes match what the server stores, keep them in sync
export const SEND_BACK_REASONS = [
  { code: "not_accurate", label: "Not accurate" },
  { code: "vague_or_unclear", label: "Vague or unclear" },
  { code: "missing_information", label: "Missing information" },
  { code: "unfair_comparison", label: "Unfair comparison" },
  { code: "not_full_life_cycle", label: "Not full life cycle" },
  { code: "not_substantiated", label: "Not substantiated" },
];

export const MIN_COMMENT = 10;
export const MIN_OVERRIDE_REASON = 10;
export const MIN_FACTS_REASON = 10;

// just a preview, the server checks again on publish and its reasons win
export function predictBlocks(item) {
  const reasons = [];
  if (item.needsOverride) {
    reasons.push("The writer kept a critical issue, so publishing needs an override.");
  }
  if (item.aiStatus && item.aiStatus !== "Ok") {
    reasons.push("The AI check has not run on this text. It runs again when you publish; if it still cannot, publishing needs an override.");
  }
  return reasons;
}

// text shown for each action in the audit trail or activity list
const ACTION_WORDS = {
  Check: "Ran a check",
  Apply: "Applied a rewrite",
  Keep: "Kept the wording",
  Submit: "Submitted for review",
  Publish: "Published",
  Override: "Overrode a block",
  Withdraw: "Withdrew a submission",
  SendBack: "Sent back",
  FactsChange: "Changed the verified facts",
  DataTransfer: "Ran a data import",
};

export const describeAction = (action, outcome) => {
  const words = ACTION_WORDS[action] || action;
  return outcome === "Blocked" ? `${words} (blocked)` : words;
};
