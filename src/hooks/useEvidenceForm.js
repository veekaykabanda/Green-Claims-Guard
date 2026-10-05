import { useState, useRef } from "react";

// handles the baseline/%/timeframe/reference fields, only needed for reduction and comparison claims. pulled out first since useIssueDecisions and useAnalysis need its state through the returned ref
export function useEvidenceForm({ setOverallStatus, setMarkReadyMessage, setIsReviewLocked, CHANGES_REQUIRED_STATUS }) {
  const [reductionInputsByIssue, setReductionInputsByIssue] = useState({});
  const reductionInputsByIssueRef = useRef({});
  reductionInputsByIssueRef.current = reductionInputsByIssue;

  const requiresEvidenceForIssue = (finding) => {
    // Trust the backend's explicit verdict first
    if (finding?.requiresEvidence != null) return Boolean(finding.requiresEvidence);
    // Fallback: only reduction/comparison claims need the evidence form
    const key = String(finding?.category || "").toLowerCase();
    return key.includes("reduction") || key.includes("compar");
  };

  const normalizeEvidenceFields = (fields = {}, finding = null) => {
    const backendFields = finding?.evidenceFields || {};
    return {
      baseline: String(fields?.baseline ?? backendFields?.baseline ?? finding?.baselineDescription ?? "").trim(),
      reductionPercentage: String(
        fields?.reductionPercentage
        ?? backendFields?.reductionPercentage
        ?? finding?.reductionPercentage
        ?? ""
      ).trim(),
      timeframe: String(fields?.timeframe ?? backendFields?.timeframe ?? finding?.timeframeOrScope ?? "").trim(),
      evidenceReference: String(
        fields?.evidenceReference
        ?? backendFields?.evidenceReference
        ?? finding?.evidenceReference
        ?? ""
      ).trim(),
    };
  };

  const isEvidenceComplete = (fields) => {
    const reductionValue = Number.parseFloat(String(fields?.reductionPercentage ?? "").trim());
    return Boolean(
      String(fields?.baseline || "").trim()
      && !Number.isNaN(reductionValue)
      && reductionValue > 0
      && String(fields?.timeframe || "").trim()
      && String(fields?.evidenceReference || "").trim()
    );
  };

  const updateIssueEvidenceField = (issueKey, field, value) => {
    setReductionInputsByIssue((prev) => ({
      ...prev,
      [issueKey]: {
        ...normalizeEvidenceFields(prev[issueKey]),
        [field]: value,
      },
    }));
    setOverallStatus(CHANGES_REQUIRED_STATUS);
    setMarkReadyMessage("");
    setIsReviewLocked(false);
  };

  const toEvidencePayload = (fields) => {
    const baseline = String(fields?.baseline || "").trim();
    const timeframe = String(fields?.timeframe || "").trim();
    const evidenceReference = String(fields?.evidenceReference || "").trim();
    const reductionPercentageRaw = String(fields?.reductionPercentage ?? "").trim();
    const reductionPercentage = Number.parseFloat(reductionPercentageRaw);

    return {
      baseline: baseline || null,
      reductionPercentage: Number.isNaN(reductionPercentage) ? null : reductionPercentage,
      timeframe: timeframe || null,
      evidenceReference: evidenceReference || null,
    };
  };

  const getPrimaryReductionPayload = () => {
    const keys = Object.keys(reductionInputsByIssue || {});
    if (!keys.length) {
      return null;
    }
    const first = reductionInputsByIssue[keys[0]];
    return {
      baselineDescription: first?.baseline,
      reductionPercentage: first?.reductionPercentage,
      timeframeOrScope: first?.timeframe,
      evidenceReference: first?.evidenceReference,
    };
  };

  return {
    reductionInputsByIssue,
    setReductionInputsByIssue,
    reductionInputsByIssueRef,
    requiresEvidenceForIssue,
    normalizeEvidenceFields,
    isEvidenceComplete,
    updateIssueEvidenceField,
    toEvidencePayload,
    getPrimaryReductionPayload,
  };
}
