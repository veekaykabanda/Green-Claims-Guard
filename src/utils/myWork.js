// combines drafts and submissions into one list per product, a live submission wins over its draft
export const STATUS_LABELS = {
  Draft: "Draft",
  InReview: "In review",
  SentBack: "Sent back",
  Published: "Published",
};

// only drafts and sent-back items can still be edited
export const isEditableStatus = (status) => status === "Draft" || status === "SentBack";

export function mergeMyWork(drafts, submissions) {
  const byId = new Map();

  for (const s of submissions || []) {
    byId.set(s.productId, {
      productId: s.productId,
      productName: s.productName,
      market: s.market,
      status: s.status,
      sendBack: s.sendBack || null,
      updatedAt: s.updatedAt || s.submittedAt,
      preview: "",
    });
  }

  for (const d of drafts || []) {
    const existing = byId.get(d.productId);
    // a live submission (in review, sent back, published) beats its draft
    if (existing && ["InReview", "SentBack", "Published"].includes(existing.status)) {
      existing.preview = d.preview || "";
      continue;
    }
    byId.set(d.productId, {
      productId: d.productId,
      productName: d.productName,
      market: d.market,
      status: "Draft",
      sendBack: null,
      updatedAt: d.savedAt,
      preview: d.preview || "",
    });
  }

  return [...byId.values()]
    // withdrawn with no draft left is just history, don't show it
    .filter((row) => row.status !== "Withdrawn")
    .sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt));
}
