import { SEND_BACK_REASONS, describeAction, predictBlocks } from "./reviewRules";

describe("predictBlocks", () => {
  test("says nothing when nothing points to a block", () => {
    expect(predictBlocks({ needsOverride: false, aiStatus: "Ok" })).toEqual([]);
  });

  test("a kept critical issue needs an override", () => {
    expect(predictBlocks({ needsOverride: true, aiStatus: "Ok" })).toEqual([expect.stringMatching(/kept a critical issue.*override/)]);
  });

  test("an AI check that did not run is named, whatever the reason it did not", () => {
    for (const aiStatus of ["Timeout", "Failed", "NotConfigured", "Skipped", "NotChecked"]) {
      expect(predictBlocks({ needsOverride: false, aiStatus })).toEqual([expect.stringMatching(/AI check has not run/)]);
    }
  });

  test("both reasons can apply at once", () => {
    expect(predictBlocks({ needsOverride: true, aiStatus: "Failed" })).toHaveLength(2);
  });
});

test("the six send-back reasons are the ones the server stores", () => {
  expect(SEND_BACK_REASONS.map((r) => r.code)).toEqual([
    "not_accurate", "vague_or_unclear", "missing_information", "unfair_comparison", "not_full_life_cycle", "not_substantiated",
  ]);
});

describe("describeAction", () => {
  test("puts every kind of action into words, and says when it was blocked", () => {
    expect(describeAction("Submit", "Ok")).toBe("Submitted for review");
    expect(describeAction("Publish", "Blocked")).toBe("Published (blocked)");
    expect(describeAction("FactsChange", "Ok")).toBe("Changed the verified facts");
    expect(describeAction("DataTransfer", "Ok")).toBe("Ran a data import");
  });

  test("an action it does not know is shown as it is, never hidden", () => {
    expect(describeAction("Something new", "Ok")).toBe("Something new");
  });
});
