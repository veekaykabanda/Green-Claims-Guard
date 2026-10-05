import { getSentenceForFinding } from "./textSimilarity";

// catches a bug where a bullet line with no punctuation got merged into the next line
describe("getSentenceForFinding", () => {
  it("does not merge an unpunctuated bullet line into the next line", () => {
    const copy = "Sustainable bamboo fibres\nMachine washable at 30 degrees.";
    const finding = { matchedPatterns: ["sustainable bamboo"] };

    const sentence = getSentenceForFinding(copy, finding);

    expect(sentence.toLowerCase()).not.toContain("machine washable");
    expect(sentence.toLowerCase()).not.toContain("30 degrees");
    expect(sentence.toLowerCase()).toContain("sustainable bamboo");
  });

  it("still finds a normal, fully punctuated sentence in a paragraph", () => {
    const copy = "This top is soft and warm. Our whole range is carbon neutral. Wash on a cool cycle.";
    const finding = { matchedPatterns: ["carbon neutral"] };

    const sentence = getSentenceForFinding(copy, finding);

    expect(sentence.trim()).toBe("Our whole range is carbon neutral.");
  });

  // ASOS style copy is one line of fragments separated by · with no punctuation, needs to split on that too
  it("does not merge ASOS-style bullet fragments separated by ·", () => {
    const copy = "Round neck · Long sleeves · Made with organic cotton · Machine wash according to label";
    const finding = { matchedPatterns: ["organic cotton"] };

    const sentence = getSentenceForFinding(copy, finding);

    expect(sentence.toLowerCase()).toContain("organic cotton");
    expect(sentence.toLowerCase()).not.toContain("round neck");
    expect(sentence.toLowerCase()).not.toContain("long sleeves");
    expect(sentence.toLowerCase()).not.toContain("machine wash");
  });
});
