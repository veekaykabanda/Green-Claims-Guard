import { useRef } from "react";

// handles issue decisions (apply/keep per issue, sentence replacement, HIGH severity rules). stuff like findingsWithMeta, unresolvedIssues and canPublish, plus the apply/keep handlers, stay in App.js since they touch this hook, useAnalysis, and the publish gate state
export function useIssueDecisions({
  inputText,
  setInputText,
  DECISION_APPLY,
  DECISION_KEEP,
  MIN_CRITICAL_KEEP_REASON,
  CHANGES_REQUIRED_STATUS,
  setIsGuardApproved,
  setIsReviewLocked,
  setMarkReadyMessage,
  setOverallStatus,
  requiresEvidenceForIssue,
  normalizeEvidenceFields,
  isEvidenceComplete,
  reductionInputsByIssue,
  issueDecisionsByKey,
  setIssueDecisionsByKey,
  findings,
}) {
  const issueDecisionsByKeyRef = useRef({});
  issueDecisionsByKeyRef.current = issueDecisionsByKey;

  // set by applySuggestion, read by useAnalysis's debounce effect, stops a re-analysis firing while an "Apply suggestion" edit is still landing in inputText
  const isApplyingRef = useRef(false);

  const getIssueKey = (finding, idx) => {
    if (finding?.issueId) {
      return String(finding.issueId).trim();
    }

    const category = String(finding?.category || "issue").toLowerCase();
    const ruleIds = Array.isArray(finding?.ruleIds) ? finding.ruleIds.filter(Boolean).join("|") : "";
    const pattern = Array.isArray(finding?.matchedPatterns)
      ? finding.matchedPatterns.filter(Boolean).join("|")
      : "";
    return `${category}::${ruleIds || pattern || idx}`;
  };

  const replaceFindingSentence = (sourceText, finding, replacementText) => {
    const original = String(sourceText || "").trim();
    const replacement = String(replacementText || "").trim();
    if (!replacement) return original;
    if (!original) return replacement;

    const primaryMatch = String((finding?.matchedPatterns || [])[0] || "").trim();

    if (primaryMatch) {
      // sentence first, only replaces the flagged sentence, stops at a line break or bullet (· or •) too so ASOS-style copy never gets merged into one giant "sentence"
      const sentenceMatches = [...original.matchAll(/[^.!?\n·•]+[.!?]*/g)];
      const targetSentence = sentenceMatches.find((m) =>
        m[0].toLowerCase().includes(primaryMatch.toLowerCase())
      );
      if (targetSentence) {
        // the matched fragment keeps whatever whitespace was around it (like around a "·"), put that back around the replacement or it ends up squashed against its neighbours
        const matched = targetSentence[0];
        const leadingSpace = matched.match(/^\s*/)[0];
        const trailingSpace = matched.match(/\s*$/)[0];
        return (
          original.slice(0, targetSentence.index) +
          leadingSpace + replacement + trailingSpace +
          original.slice(targetSentence.index + matched.length)
        ).trim();
      }

      // Fallback: line-level replacement (for bullet/heading structure)
      const lines = original.split("\n");
      const targetIdx = lines.findIndex((l) =>
        l.toLowerCase().includes(primaryMatch.toLowerCase())
      );
      if (targetIdx >= 0) {
        const newLines = [...lines];
        newLines[targetIdx] = replacement;
        return newLines.join("\n");
      }
    }

    // no match found, just return the original so nothing gets duplicated
    return original;
  };

  const updateIssueDecision = (issueKey, updates) => {
    setIssueDecisionsByKey((prev) => ({
      ...prev,
      [issueKey]: {
        ...(prev[issueKey] || {}),
        ...updates,
      },
    }));
  };

  const applySuggestion = (
    suggestion,
    reductionPayload = null,
    issueKey = null,
    finding = null,
    resolvedSentenceSignature = ""
  ) => {
    const cleanText = String(suggestion || "").trim();
    if (!cleanText) return;

    const nextDescription = replaceFindingSentence(inputText, finding, cleanText);
    if (!nextDescription) return;

    isApplyingRef.current = true;
    setTimeout(() => {
      isApplyingRef.current = false;
    }, 600);

    setInputText(nextDescription);
    setIsGuardApproved(false);
    setIsReviewLocked(false);
    setMarkReadyMessage("");
    setOverallStatus(CHANGES_REQUIRED_STATUS);

    if (issueKey) {
      setIssueDecisionsByKey((prev) => ({
        ...prev,
        [issueKey]: {
          ...(prev[issueKey] || {}),
          userDecision: DECISION_APPLY,
          userJustification: "",
          resolvedSentenceSignature: String(resolvedSentenceSignature || prev[issueKey]?.resolvedSentenceSignature || "").trim(),
        },
      }));
    }
    // no re-analysis here, user works through issues one by one, gets auto approved once they're all resolved
  };

  const issueStates = findings.map((finding, idx) => {
    const issueKey = getIssueKey(finding, idx);
    const state = issueDecisionsByKey[issueKey] || {};
    const requiresEvidence = requiresEvidenceForIssue(finding);
    const severity = String(finding?.severity || "").toLowerCase();
    const evidenceFields = normalizeEvidenceFields(reductionInputsByIssue[issueKey], finding);
    const userDecision = state.userDecision || null;
    const userJustification = String(state.userJustification || "").trim();

    // evidence only blocks completion when the user keeps a reduction/comparison claim, applying the AI rewrite always resolves it, no form needed
    const evidenceComplete = !requiresEvidence
      || userDecision === DECISION_APPLY
      || (userDecision === DECISION_KEEP && isEvidenceComplete(evidenceFields));

    const hasDecision =
      userDecision === DECISION_APPLY
      || userDecision === DECISION_KEEP;
    const decisionComplete = hasDecision && evidenceComplete;

    const isHighSeverity = severity === "high";
    const isMediumSeverity = severity === "medium";
    // a HIGH issue gets resolved by applying the suggestion or keeping it with a real reason (at least MIN_CRITICAL_KEEP_REASON chars), matches the server's ComplianceOrchestrator exactly, including that a fact the copy contradicts is just false and can't be kept
    const isCriticalKeepValid =
      userDecision === DECISION_KEEP
      && finding?.category !== "product_facts_mismatch"
      && userJustification.length >= MIN_CRITICAL_KEEP_REASON;
    const highResolved = !isHighSeverity || userDecision === DECISION_APPLY || isCriticalKeepValid;

    return {
      issueKey,
      requiresEvidence,
      evidenceFields,
      evidenceComplete,
      userDecision,
      userJustification,
      decisionComplete,
      isHighSeverity,
      isMediumSeverity,
      highResolved,
    };
  });

  const issueStateByKey = issueStates.reduce((acc, issueState) => {
    acc[issueState.issueKey] = issueState;
    return acc;
  }, {});

  return {
    issueDecisionsByKeyRef,
    isApplyingRef,
    getIssueKey,
    updateIssueDecision,
    applySuggestion,
    issueStates,
    issueStateByKey,
  };
}
