import { isEditableStatus, mergeMyWork } from "./myWork";

const draft = (over = {}) => ({ productId: "d1", productName: "Draft dress", market: "UK", savedAt: "2026-09-25T10:00:00Z", preview: "Soft linen.", ...over });
const submission = (over = {}) => ({
  productId: "s1", productName: "Wrap cardigan", market: "EU", status: "InReview", submittedAt: "2026-09-24T09:00:00Z",
  updatedAt: "2026-09-24T09:00:00Z", sendBack: null, ...over,
});

describe("mergeMyWork", () => {
  test("lists drafts and submissions together, newest first, each with its own status", () => {
    const rows = mergeMyWork([draft()], [submission({ status: "Published", updatedAt: "2026-09-20T09:00:00Z" })]);
    expect(rows.map((r) => [r.productId, r.status])).toEqual([["d1", "Draft"], ["s1", "Published"]]);
  });

  test("a product with both a draft and a live submission shows the submission's status once", () => {
    const rows = mergeMyWork([draft({ productId: "s1" })], [submission({ status: "SentBack", sendBack: { reasonLabel: "Not substantiated" } })]);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({ productId: "s1", status: "SentBack", preview: "Soft linen." });
    expect(rows[0].sendBack.reasonLabel).toBe("Not substantiated");
  });

  test("a withdrawn submission whose draft came back is a draft again", () => {
    const rows = mergeMyWork([draft({ productId: "s1" })], [submission({ status: "Withdrawn" })]);
    expect(rows).toEqual([expect.objectContaining({ productId: "s1", status: "Draft" })]);
  });

  test("a withdrawn submission with no draft left is only history and is not listed", () => {
    expect(mergeMyWork([], [submission({ status: "Withdrawn" })])).toEqual([]);
  });

  test("copes with nothing at all", () => {
    expect(mergeMyWork(undefined, undefined)).toEqual([]);
  });
});

test("only a draft or a sent-back product can be edited", () => {
  expect(["Draft", "SentBack", "InReview", "Published"].map(isEditableStatus)).toEqual([true, true, false, false]);
});
