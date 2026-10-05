import { useRef, useCallback } from "react";
import { normalizeForSimilarity, getSentenceForFinding } from "../utils/textSimilarity";

// does the analyzeText API call. depends on useIssueDecisions's output, so analysis state stays in App.js to avoid a call-order cycle between the two hooks
export function useAnalysis({
  API_BASE,
  CHANGES_REQUIRED_STATUS,
  DECISION_APPLY,
  DECISION_KEEP,
  getAuthHeaders,
  productFacts,
  requiresEvidenceForIssue,
  normalizeEvidenceFields,
  reductionInputsByIssueRef,
  getIssueKey,
  issueDecisionsByKeyRef,
  analysisRef,
  setAnalysis,
  setIsAiAnalysis,
  setIsAnalyzing,
  setOverallStatus,
  setMarkReadyMessage,
  setIsReviewLocked,
  analysisContextRef,
}) {
  const analyzeTextRef = useRef(null);

  const normalizeReductionPayload = (payload) => {
    if (!payload || typeof payload !== "object") {
      return {};
    }

    const normalized = {};

    const baselineDescription = String(payload.baselineDescription || "").trim();
    if (baselineDescription) {
      normalized.baselineDescription = baselineDescription;
    }

    const reductionRaw = String(payload.reductionPercentage ?? "").trim();
    const reductionValue = Number.parseFloat(reductionRaw);
    if (!Number.isNaN(reductionValue) && reductionValue > 0) {
      normalized.reductionPercentage = reductionValue;
    }

    const timeframeOrScope = String(payload.timeframeOrScope || "").trim();
    if (timeframeOrScope) {
      normalized.timeframeOrScope = timeframeOrScope;
    }

    const evidenceReference = String(payload.evidenceReference || "").trim();
    if (evidenceReference) {
      normalized.evidenceReference = evidenceReference;
    }

    return normalized;
  };

  const analyzeText = useCallback(
    async (text, reductionPayload = null, rulesOnly = false, trigger = "live") => {
      setIsAnalyzing(true);
      if (rulesOnly) setIsAiAnalysis(false);
      try {
        const buildIssueDecisionsPayload = (currentText) => {
          const findings = analysisRef.current?.groupedFindings || [];
          if (!findings.length) {
            return [];
          }

          return findings
            .map((finding, idx) => {
              const issueKey = getIssueKey(finding, idx);
              const state = issueDecisionsByKeyRef.current[issueKey] || {};
              const userDecision = state.userDecision || null;

              if (userDecision !== DECISION_APPLY && userDecision !== DECISION_KEEP) {
                return null;
              }

              const requiresEvidence = requiresEvidenceForIssue(finding);
              const evidence = normalizeEvidenceFields(reductionInputsByIssueRef.current[issueKey], finding);
              const parsedReduction = Number.parseFloat(String(evidence?.reductionPercentage ?? "").trim());

              const resolvedSentenceSignature = String(
                state.resolvedSentenceSignature
                || normalizeForSimilarity(getSentenceForFinding(currentText, finding))
              ).trim();

              return {
                issueId: finding?.issueId || issueKey,
                severity: String(finding?.severity || "").toUpperCase(),
                userDecision,
                userJustification: String(state.userJustification || "").trim() || null,
                requiresEvidence,
                evidenceFields: requiresEvidence
                  ? {
                    baseline: String(evidence?.baseline || "").trim() || null,
                    reductionPercentage: Number.isNaN(parsedReduction) ? null : parsedReduction,
                    timeframe: String(evidence?.timeframe || "").trim() || null,
                    evidenceReference: String(evidence?.evidenceReference || "").trim() || null,
                  }
                  : null,
                resolvedSentenceSignature: resolvedSentenceSignature || null,
              };
            })
            .filter(Boolean);
        };

        const issueDecisions = buildIssueDecisionsPayload(text);
        const industryValue = "fashion";
        const factsPayload = {
          materialComposition: productFacts.materialComposition.trim() || null,
          certificationsHeld: productFacts.certificationsHeld.length ? productFacts.certificationsHeld : null,
          additionalFacts: productFacts.additionalFacts.trim() || null,
        };
        const hasAnyFacts = factsPayload.materialComposition || factsPayload.certificationsHeld || factsPayload.additionalFacts;
        const payload = {
          text,
          rulesOnly,
          // manual checks get logged server side, live typing checks don't
          trigger,
          // which product this is, its name (checked same as the description), and the market's rules
          ...(analysisContextRef?.current?.productId ? { productId: analysisContextRef.current.productId } : {}),
          ...(String(analysisContextRef?.current?.productName || "").trim() ? { productName: String(analysisContextRef.current.productName).trim() } : {}),
          // the Product Details card (category, subcategory, tags), checked the same way as the name
          ...(String(analysisContextRef?.current?.primaryCategory || "").trim() ? { productCategory: String(analysisContextRef.current.primaryCategory).trim() } : {}),
          ...(String(analysisContextRef?.current?.secondaryCategory || "").trim() ? { productSubcategory: String(analysisContextRef.current.secondaryCategory).trim() } : {}),
          ...(String(analysisContextRef?.current?.productTags || "").trim() ? { productTags: String(analysisContextRef.current.productTags).trim() } : {}),
          ...(analysisContextRef?.current?.market ? { market: analysisContextRef.current.market } : {}),
          ...(industryValue ? { industry: industryValue } : {}),
          ...normalizeReductionPayload(reductionPayload),
          ...(issueDecisions.length ? { issueDecisions } : {}),
          ...(hasAnyFacts ? { productFacts: factsPayload } : {}),
        };

        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), 30000);
        let res;
        try {
          res = await fetch(`${API_BASE}/api/analyze`, {
            method: "POST",
            headers: await getAuthHeaders({ "Content-Type": "application/json" }),
            body: JSON.stringify(payload),
            signal: controller.signal,
          });
        } finally {
          clearTimeout(timeoutId);
        }

        if (!res.ok) {
          // the API puts the real reason in the response body, res.statusText alone is just "Bad Request" and loses it
          let message = res.statusText || "Analysis failed";
          try {
            const errorBody = await res.json();
            if (errorBody?.message) message = errorBody.message;
          } catch {
            // body wasn't JSON (or already used up), just keep the statusText fallback
          }
          const rejection = new Error(message);
          // the server refusing the request (bad input, rate limit) is different from never reaching it, needs different wording
          rejection.isServerRejection = res.status >= 400 && res.status < 500;
          throw rejection;
        }
        const data = await res.json();
        setAnalysis(data);
        // asking for AI isn't the same as it working, check the API's own aiStatus for what really happened. trusting "we asked" over "it worked" is what let a failed AI call show its fallback text as if it were a real AI rewrite
        setIsAiAnalysis(!rulesOnly && data?.aiStatus === "Ok");
        setOverallStatus(data?.overallStatus || CHANGES_REQUIRED_STATUS);
        setMarkReadyMessage("");
        setIsReviewLocked(false);
      } catch (err) {
        const errorMsg = err.message || "Could not reach analysis service";
        // a rejected request (like going over the 5,000 character limit) isn't a connection problem, showing "start the backend" would send the writer chasing the wrong fix
        const isLocal = /localhost|127\.0\.0\.1/.test(API_BASE);
        const { category, regulation, aiExplanation } = err.isServerRejection
          ? {
            category: "Input rejected",
            regulation: "Fix the description and try again.",
            aiExplanation: errorMsg,
          }
          : {
            category: "Connection Error",
            regulation: `Backend should be running at ${API_BASE}`,
            aiExplanation: isLocal
              ? `Check backend on ${API_BASE}. Start API with: cd backend-dotnet/GreenClaimsGuard.Api && dotnet run`
              : `Could not reach ${API_BASE}. Check the API is running and try again.`,
          };
        setAnalysis({
          overallStatus: CHANGES_REQUIRED_STATUS,
          overallRisk: "High",
          trafficLight: "RED",
          complianceScore: 0,
          totalIssues: 1,
          groupedFindings: [
            {
              category,
              severity: "high",
              regulation,
              explanation: errorMsg,
              matchedPatterns: err.isServerRejection ? ["Input Rejected"] : ["Connection Failed"],
              ruleIds: ["ERROR-001"],
              count: 1,
            },
          ],
          aiExplanation,
          references: [],
        });
        setOverallStatus(CHANGES_REQUIRED_STATUS);
        setIsReviewLocked(false);
      }
      setIsAnalyzing(false);
    },
    [
      analysisContextRef,
      API_BASE,
      CHANGES_REQUIRED_STATUS,
      DECISION_APPLY,
      DECISION_KEEP,
      getAuthHeaders,
      productFacts.materialComposition,
      productFacts.certificationsHeld,
      productFacts.additionalFacts,
      requiresEvidenceForIssue,
      normalizeEvidenceFields,
      reductionInputsByIssueRef,
      getIssueKey,
      issueDecisionsByKeyRef,
      analysisRef,
      setAnalysis,
      setIsAiAnalysis,
      setIsAnalyzing,
      setOverallStatus,
      setMarkReadyMessage,
      setIsReviewLocked,
    ]
  );

  analyzeTextRef.current = analyzeText;

  return { analyzeText, analyzeTextRef };
}
