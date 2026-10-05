export const normalizeForSimilarity = (value) => {
  return String(value || "")
    .toLowerCase()
    .replace(/[^a-z0-9\s]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
};

export const calculateSimilarity = (left, right) => {
  const normalizedLeft = normalizeForSimilarity(left);
  const normalizedRight = normalizeForSimilarity(right);

  if (!normalizedLeft || !normalizedRight) return 0;
  if (normalizedLeft === normalizedRight) return 1;

  const leftTokens = normalizedLeft.split(" ").filter(Boolean);
  const rightTokens = normalizedRight.split(" ").filter(Boolean);
  if (!leftTokens.length || !rightTokens.length) return 0;

  const leftCounts = new Map();
  leftTokens.forEach((token) => {
    leftCounts.set(token, (leftCounts.get(token) || 0) + 1);
  });

  let intersection = 0;
  rightTokens.forEach((token) => {
    const count = leftCounts.get(token) || 0;
    if (count > 0) {
      intersection += 1;
      leftCounts.set(token, count - 1);
    }
  });

  return (2 * intersection) / (leftTokens.length + rightTokens.length);
};

export const getSentenceForFinding = (text, finding) => {
  const source = String(text || "").trim();
  if (!source) return "";

  // splits on line breaks and bullet points too, so bullet style copy doesn't get merged into one sentence
  const sentences = source.match(/[^.!?\n·•]+[.!?]*/g) || [source];
  const primaryMatch = String((finding?.matchedPatterns || [])[0] || "").trim();
  if (!primaryMatch) {
    return String(sentences[0] || "").trim();
  }

  const matchedSentence = sentences.find((sentence) =>
    sentence.toLowerCase().includes(primaryMatch.toLowerCase())
  );

  return String(matchedSentence || sentences[0] || "").trim();
};

export const escapeRegex = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
