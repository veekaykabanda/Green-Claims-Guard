import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import "./App.css";
import {
  AlertTriangle,
  Bell,
  Boxes,
  CheckCircle,
  ClipboardList,
  ExternalLink,
  LayoutDashboard,
  Lightbulb,
  Lock,
  Mail,
  Moon,
  Sun,
  UserCircle,
  Settings,
  Shield,
  ShieldCheck,
} from "lucide-react";

import HelpView from "./views/HelpView";
import AccountSettingsView from "./views/AccountSettingsView";
import ExitView from "./views/ExitView";
import ActivityView from "./views/ActivityView";
import DataTransferView from "./views/DataTransferView";
import PendingReviewView from "./views/PendingReviewView";
import ReviewPanel from "./views/ReviewPanel";
import VerifiedFactsView from "./views/VerifiedFactsView";
import AuditTrailView from "./views/AuditTrailView";
import AnalysisHistoryView from "./views/AnalysisHistoryView";
import ProductInventoryView from "./views/ProductInventoryView";
import DashboardOverviewView from "./views/DashboardOverviewView";
import ReportsView from "./views/ReportsView";
import { normalizeForSimilarity, calculateSimilarity, getSentenceForFinding, escapeRegex } from "./utils/textSimilarity";
import { hasPermission } from "./utils/permissions";
import { getPersona } from "./utils/personas";
import { useProductForm } from "./hooks/useProductForm";
import { useEvidenceForm } from "./hooks/useEvidenceForm";
import { useIssueDecisions } from "./hooks/useIssueDecisions";
import { useAnalysis } from "./hooks/useAnalysis";
import { useAsync } from "./hooks/useAsync";
import { useMyProduct, STATUS } from "./hooks/useMyProduct";
import { createApi } from "./services/api";
import MyHomeView from "./views/MyHomeView";
import MyWorkView from "./views/MyWorkView";
import ProductStatusBanners from "./views/ProductStatusBanners";
import VerifiedFactsPanel from "./views/VerifiedFactsPanel";

const MATERIAL_SIMILARITY_THRESHOLD = 0.85;
// matches ComplianceOrchestrator.MinCriticalKeepReason on the server, a short "keep" reason gets refused there no matter what this checks
const MIN_CRITICAL_KEEP_REASON = 15;

// matches the server's 5,000 character cap (Program.cs, product_drafts.text) so a writer sees the limit before submitting, not just after the server rejects it
const MAX_DESCRIPTION_LENGTH = 5000;

const PINNED_REGULATION_UPDATES = [
  {
    id: "pinned-cma-fashion-2023",
    title: "CMA secures green pledges from ASOS, Boohoo and Asda",
    url: "https://www.gov.uk/government/news/cma-secures-green-pledges-from-major-fashion-brands",
    source: "CMA",
    publishedDate: "2023-09-11T00:00:00Z",
    affectedClaimTypes: ["Fashion & apparel", "Sustainability claims", "Greenwashing"],
  },
  {
    id: "pinned-asa-bamboo-2021",
    title: "ASA ruling: 'bamboo' fabric claims found misleading",
    url: "https://www.asa.org.uk/advice-online/environmental-claims.html",
    source: "ASA",
    publishedDate: "2021-06-16T00:00:00Z",
    affectedClaimTypes: ["Bamboo fabric", "Fashion & apparel", "Misleading claims"],
  },
  {
    id: "pinned-cma-carbon-neutral-2023",
    title: "CMA investigation into 'carbon neutral' and 'net zero' claims",
    url: "https://www.gov.uk/cma-cases/environmental-claims-on-household-essentials",
    source: "CMA",
    publishedDate: "2023-01-26T00:00:00Z",
    affectedClaimTypes: ["Carbon neutral", "Net zero", "Greenwashing"],
  },
  {
    id: "pinned-cma-green-claims-code-2021",
    title: "CMA Green Claims Code: 6 principles for environmental claims",
    url: "https://www.gov.uk/government/publications/green-claims-code-making-environmental-claims",
    source: "CMA",
    publishedDate: "2021-09-20T00:00:00Z",
    affectedClaimTypes: ["Sustainability claims", "Greenwashing"],
  },
];

const GreenClaimsGuard = ({ user, onLogout, getToken, onSessionExpired }) => {
  const {
    productName, setProductName,
    market, setMarket,
    primaryCategory, setPrimaryCategory,
    secondaryCategory, setSecondaryCategory,
    productTags, setProductTags,
  } = useProductForm();
  const [inputText, setInputText] = useState("");
  const [analysis, setAnalysis] = useState(null);
  const [isAiAnalysis, setIsAiAnalysis] = useState(false);
  const analysisRef = React.useRef(null);
  const [isAnalyzing, setIsAnalyzing] = useState(false);
  const [isGuardApproved, setIsGuardApproved] = useState(false);
  const [lastApprovedText, setLastApprovedText] = useState("");
  const [activeTab, setActiveTab] = useState("description");
  const [productFacts, setProductFacts] = useState({ materialComposition: "", certificationsHeld: [], additionalFacts: "" });
  const [showProductFacts, setShowProductFacts] = useState(false);
  const [showManualFacts, setShowManualFacts] = useState(false); // always starts hidden
  const [uploadingForClaim, setUploadingForClaim] = useState(null);
  const [uploadedForClaims, setUploadedForClaims] = useState({});
  const [validationErrorsByClaim, setValidationErrorsByClaim] = useState({});
  const [issueDecisionsByKey, setIssueDecisionsByKey] = useState({});
  const [showRewriteByKey, setShowRewriteByKey] = useState({});
  const [keepJustPendingByKey, setKeepJustPendingByKey] = useState({});
  const [keepJustTextByKey, setKeepJustTextByKey] = useState({});
  const [overallStatus, setOverallStatus] = useState("CHANGES_REQUIRED");
  const [markReadyMessage, setMarkReadyMessage] = useState("");
  const [isReviewLocked, setIsReviewLocked] = useState(false);
  // whether the writer has pressed Submit yet, the reason for a blocked submit only shows once they've tried, not while still typing
  const [submitAttempted, setSubmitAttempted] = useState(false);
  const [isChromeScrolled, setIsChromeScrolled] = useState(false);
  // The app-wide theme. Persisted so the choice survives a reload, not just the session.
  const [theme, setTheme] = useState(() => {
    try {
      return localStorage.getItem("gcg-theme") === "dark" ? "dark" : "light";
    } catch {
      return "light";
    }
  });
  const toggleTheme = () => {
    setTheme((current) => {
      const next = current === "dark" ? "light" : "dark";
      try {
        localStorage.setItem("gcg-theme", next);
      } catch {
        // private browsing or blocked storage, the toggle still works for this session, just doesn't persist
      }
      return next;
    });
  };
  // document.documentElement isn't part of React's JSX tree, so the theme gets set as a side effect instead of a prop
  useEffect(() => {
    document.documentElement.setAttribute("data-theme", theme);
  }, [theme]);
  const [activeSidebarItem, setActiveSidebarItem] = useState("product");
  const [activeSidebarSubItem, setActiveSidebarSubItem] = useState("add-product");
  const [dashboardStats, setDashboardStats] = useState(null);
  const [recentClaims, setRecentClaims] = useState([]);
  const [regUpdates, setRegUpdates] = useState([]);
  const [isLoadingStats, setIsLoadingStats] = useState(false);
  const [isLoadingClaims, setIsLoadingClaims] = useState(false);
  const [isLoadingUpdates, setIsLoadingUpdates] = useState(false);
  const [isSeeding, setIsSeeding] = useState(false);
  const [seedResult, setSeedResult] = useState(null);
  const [pendingReviews, setPendingReviews] = useState([]);
  const [isLoadingPendingReviews, setIsLoadingPendingReviews] = useState(false);
  // a failed reload used to look normal but wrong, with nothing saying it failed. now each one says so
  const [statsError, setStatsError] = useState("");
  const [claimsError, setClaimsError] = useState("");
  const [regUpdatesError, setRegUpdatesError] = useState("");
  const [pendingReviewsError, setPendingReviewsError] = useState("");
  // The submission a Senior Editor has open, what to tell them after acting on one, and the product whose facts are open.
  const [reviewItem, setReviewItem] = useState(null);
  const [queueMessage, setQueueMessage] = useState("");
  const [factsFocus, setFactsFocus] = useState(null);
  const [activeTopbarMenu, setActiveTopbarMenu] = useState(null); // 'bell', 'mail', 'user'
  const [isWithdrawing, setIsWithdrawing] = useState(false);

  const CHANGES_REQUIRED_STATUS = "CHANGES_REQUIRED";
  const READY_TO_PUBLISH_STATUS = "READY_TO_PUBLISH_SUBJECT_TO_REVIEW";
  const DECISION_APPLY = "APPLIED_SUGGESTION";
  const DECISION_KEEP = "KEPT_ORIGINAL_WITH_JUSTIFICATION";

  const {
    reductionInputsByIssue,
    setReductionInputsByIssue,
    reductionInputsByIssueRef,
    requiresEvidenceForIssue,
    normalizeEvidenceFields,
    isEvidenceComplete,
    updateIssueEvidenceField,
    toEvidencePayload,
    getPrimaryReductionPayload,
  } = useEvidenceForm({ setOverallStatus, setMarkReadyMessage, setIsReviewLocked, CHANGES_REQUIRED_STATUS });

  const {
    issueDecisionsByKeyRef,
    isApplyingRef,
    getIssueKey,
    updateIssueDecision,
    applySuggestion,
    issueStates,
    issueStateByKey,
  } = useIssueDecisions({
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
    findings: analysis?.groupedFindings || [],
  });

  const API_BASE = process.env.REACT_APP_API_URL || "http://localhost:8080";

  // X-User-Role header is gone on purpose, that was the auth bypass. the JWT is the only source of truth now
  const getAuthHeaders = useCallback(async (extra = {}) => {
    const token = getToken ? await getToken() : null;
    return {
      ...extra,
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    };
  }, [getToken]);
  const normalizedInputText = (inputText || "").trim();
  const normalizedApprovedText = (lastApprovedText || "").trim();
  const textChangedSinceApproval = Boolean(normalizedApprovedText) && normalizedInputText !== normalizedApprovedText;

  const isCopywriter = getPersona(user).id === "copywriter";
  // A ref, so a new function from the parent on each render never rebuilds the api and reloads every list.
  const onSessionExpiredRef = useRef(onSessionExpired);
  onSessionExpiredRef.current = onSessionExpired;
  const api = useMemo(
    () => createApi({ baseUrl: API_BASE, getAuthHeaders, onUnauthorized: () => onSessionExpiredRef.current?.() }),
    [API_BASE, getAuthHeaders]
  );

  // the product being written, on the server, which one it is, where it stands, whether it's saved
  const myProduct = useMyProduct({ api, name: productName, text: inputText, market });
  // A product's identity is this id, never its name.
  const currentProductId = myProduct.productId;
  const analysisContextRef = useRef({});
  analysisContextRef.current = {
    productId: currentProductId,
    productName,
    market,
    primaryCategory,
    secondaryCategory,
    productTags,
  };
  const lastAiKeyRef = useRef("");

  // A Copywriter's own Home and lists. A Senior Editor's screens read the team's numbers instead.
  const myOverview = useAsync(() => (isCopywriter ? api.getMyOverview() : Promise.resolve(null)), [api, isCopywriter]);
  const mySubmissions = useAsync(() => (isCopywriter ? api.getMySubmissions() : Promise.resolve([])), [api, isCopywriter]);
  const myDrafts = useAsync(() => (isCopywriter ? api.getMyDrafts() : Promise.resolve([])), [api, isCopywriter]);
  const myActivity = useAsync(() => api.getMyActivity(), [api]);
  const reloadMyActivity = myActivity.reload;
  const reloadMyOverview = myOverview.reload;
  const reloadMySubmissions = mySubmissions.reload;
  const reloadMyDrafts = myDrafts.reload;
  const reloadMyWork = useCallback(() => {
    reloadMyOverview();
    reloadMySubmissions();
    reloadMyDrafts();
  }, [reloadMyOverview, reloadMySubmissions, reloadMyDrafts]);

  useEffect(() => {
    const onScroll = () => {
      setIsChromeScrolled(window.scrollY > 28);
    };

    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

  const fetchDashboardStats = useCallback(async () => {
    setIsLoadingStats(true);
    setStatsError("");
    try {
      const res = await fetch(`${API_BASE}/api/dashboard/overview`, {
        headers: await getAuthHeaders(),
      });
      if (res.ok) {
        const data = await res.json();
        setDashboardStats(data);
      } else {
        setStatsError(res.status === 401 ? "Your session has expired. Sign in again." : "Could not load the dashboard stats.");
      }
    } catch (err) {
      console.error("Failed to fetch dashboard stats", err);
      setStatsError("Could not reach the server.");
    } finally {
      setIsLoadingStats(false);
    }
  }, [API_BASE, getAuthHeaders]);

  const fetchRecentClaims = useCallback(async () => {
    setIsLoadingClaims(true);
    setClaimsError("");
    try {
      const res = await fetch(`${API_BASE}/api/claims/recent`, {
        headers: await getAuthHeaders(),
      });
      if (res.ok) {
        const data = await res.json();
        setRecentClaims(data);
      } else {
        setClaimsError(res.status === 401 ? "Your session has expired. Sign in again." : "Could not load the product inventory.");
      }
    } catch (err) {
      console.error("Failed to fetch recent claims", err);
      setClaimsError("Could not reach the server.");
    } finally {
      setIsLoadingClaims(false);
    }
  }, [API_BASE, getAuthHeaders]);

  const fetchRegUpdates = useCallback(async () => {
    setIsLoadingUpdates(true);
    setRegUpdatesError("");
    try {
      const res = await fetch(`${API_BASE}/api/regulation-updates`, {
        headers: await getAuthHeaders(),
      });
      if (res.ok) {
        const data = await res.json();
        setRegUpdates(data);
      } else {
        setRegUpdatesError(res.status === 401 ? "Your session has expired. Sign in again." : "Could not load regulatory updates.");
      }
    } catch (err) {
      console.error("Failed to fetch regulation updates", err);
      setRegUpdatesError("Could not reach the server.");
    } finally {
      setIsLoadingUpdates(false);
    }
  }, [API_BASE, getAuthHeaders]);


  const [isExportingAudit, setIsExportingAudit] = useState(false);
  const [auditExportError, setAuditExportError] = useState(null);

  const exportAuditCsv = useCallback(async () => {
    setIsExportingAudit(true);
    setAuditExportError(null);
    try {
      const res = await fetch(`${API_BASE}/api/audit/export`, { headers: await getAuthHeaders() });
      if (!res.ok) {
        throw new Error(res.status === 403 ? "Only a Senior Editor can export the audit trail." : "The export failed. Try again.");
      }
      const url = URL.createObjectURL(await res.blob());
      const link = document.createElement("a");
      link.href = url;
      link.download = `audit-trail-${new Date().toISOString().slice(0, 10)}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (err) {
      setAuditExportError(err.message || "The export failed. Try again.");
    } finally {
      setIsExportingAudit(false);
    }
  }, [API_BASE, getAuthHeaders]);

  const runSeedImport = useCallback(async () => {
    setIsSeeding(true);
    setSeedResult(null);
    try {
      const res = await fetch(`${API_BASE}/api/data-transfer/seed`, {
        method: "POST",
        headers: await getAuthHeaders(),
      });
      if (res.ok) {
        const data = await res.json();
        setSeedResult(data);
      } else {
        // refused (like outside Development), say why in the server's own words
        const problem = await res.json().catch(() => null);
        setSeedResult({ success: false, message: problem?.detail || problem?.message || "The import failed." });
      }
    } catch (err) {
      console.error("Seed import failed", err);
      setSeedResult({ success: false, message: "Network error during import" });
    } finally {
      setIsSeeding(false);
    }
  }, [API_BASE, getAuthHeaders]);

  const fetchPendingReviews = useCallback(async () => {
    setIsLoadingPendingReviews(true);
    setPendingReviewsError("");
    try {
      const res = await fetch(`${API_BASE}/api/claims/pending-review`, {
        headers: await getAuthHeaders(),
      });
      if (res.ok) {
        const data = await res.json();
        setPendingReviews(data);
      } else {
        setPendingReviewsError(res.status === 401 ? "Your session has expired. Sign in again." : "Could not load Pending Review.");
      }
    } catch (err) {
      console.error("Failed to fetch pending reviews", err);
      setPendingReviewsError("Could not reach the server.");
    } finally {
      setIsLoadingPendingReviews(false);
    }
  }, [API_BASE, getAuthHeaders]);

  // after a Senior Editor publishes or sends back a submission, say what happened and show the queue again
  const handleReviewDone = async (message) => {
    setQueueMessage(message);
    setReviewItem(null);
    await fetchPendingReviews();
  };

  // Leaving a screen closes anything open on it, except the product a Senior Editor just chose to open the facts of.
  const factsToOpenRef = useRef(null);
  useEffect(() => {
    setReviewItem(null);
    setFactsFocus(factsToOpenRef.current);
    factsToOpenRef.current = null;
  }, [activeSidebarItem]);

  useEffect(() => {
    if (!isCopywriter && (activeSidebarItem === "home" || activeSidebarItem === "overview")) {
      fetchDashboardStats();
    }
    if (isCopywriter && (activeSidebarItem === "home" || (activeSidebarItem === "product" && activeSidebarSubItem === "my-products"))) {
      reloadMyWork();
    }
    if (!isCopywriter && activeSidebarItem === "product" && activeSidebarSubItem === "product-lists") {
      fetchRecentClaims();
    }
    if (activeSidebarItem === "reports") {
      fetchRegUpdates();
    }
    if (activeSidebarItem === "pending-review") {
      setQueueMessage("");
      fetchPendingReviews();
    }
    if (activeSidebarItem === "activity") {
      reloadMyActivity();
    }
  }, [isCopywriter, reloadMyWork, reloadMyActivity, activeSidebarItem, activeSidebarSubItem, fetchDashboardStats, fetchRecentClaims, fetchRegUpdates, fetchPendingReviews]);


  const cleanSuggestionText = (value) => {
    return String(value || "")
      .replace(/\b(\w+)\s+\1\b/gi, "$1")
      .replace(/\s+/g, " ")
      .trim();
  };

  const getSingleSentence = (value) => {
    const cleaned = cleanSuggestionText(value);
    if (!cleaned) return "";
    const match = cleaned.match(/[^.!?]+[.!?]?/);
    return (match?.[0] || cleaned).trim();
  };

  // like getSingleSentence but splits on newlines instead of punctuation, so periods like "cert. no." survive and nothing multi-line sneaks into the description
  const getCleanRewrite = (value) => {
    const cleaned = cleanSuggestionText(value);
    if (!cleaned) return "";
    return cleaned.split("\n").map((l) => l.trim()).find((l) => l) || cleaned;
  };

  const isValidatingEvidenceRef = React.useRef(false);

  const { analyzeText, analyzeTextRef } = useAnalysis({
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
  });

  useEffect(() => {
    if (!myProduct.editable) return;
    if (isGuardApproved && !textChangedSinceApproval) return;
    if (isApplyingRef.current) return;

    if (inputText.length > 10) {
      // the rules check as you type, after a short pause, gives instant feedback while still typing
      const rulesTimer = setTimeout(() => {
        analyzeTextRef.current(inputText, null, true);
      }, 400);
      return () => clearTimeout(rulesTimer);
    }
    setAnalysis(null);
  }, [myProduct.editable, market, inputText, isGuardApproved, textChangedSinceApproval, isApplyingRef, analyzeTextRef]);

  useEffect(() => {
    if (!myProduct.editable) return;
    if (isGuardApproved && !textChangedSinceApproval) return;
    if (isApplyingRef.current) return;
    if (inputText.trim().length <= 10) return;

    // the full check (rules + AI) runs on its own once typing pauses, debounced longer than the rules-only check and deduped via lastAiKeyRef (shared with handleDescriptionBlur) so the real AI call fires once per distinct text, not every keystroke pause
    const aiTimer = setTimeout(() => {
      const key = `${market}\n${inputText.trim()}`;
      if (lastAiKeyRef.current === key) return;
      lastAiKeyRef.current = key;
      analyzeTextRef.current(inputText, getPrimaryReductionPayload(), false);
    }, 1500);
    return () => clearTimeout(aiTimer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [myProduct.editable, market, inputText, isGuardApproved, textChangedSinceApproval, isApplyingRef, analyzeTextRef]);

  useEffect(() => {
    if (isGuardApproved && textChangedSinceApproval) {
      setIsGuardApproved(false);
    }
  }, [isGuardApproved, textChangedSinceApproval]);

  useEffect(() => {
    analysisRef.current = analysis;
    // issueDecisionsByKeyRef stays in sync inside useIssueDecisions, reductionInputsByIssueRef inside useEvidenceForm
    const findings = analysis?.groupedFindings || [];
    if (!findings.length) {
      return;
    }

    setOverallStatus(analysis?.overallStatus || CHANGES_REQUIRED_STATUS);

    setIssueDecisionsByKey((prev) => {
      const next = {};
      findings.forEach((finding, idx) => {
        const key = getIssueKey(finding, idx);
        const existing = prev[key] || {};
        const currentSentenceSignature = normalizeForSimilarity(getSentenceForFinding(inputText, finding));
        const existingResolvedSignature = String(existing.resolvedSentenceSignature || "").trim();
        const existingDecision = existing.userDecision ?? null;
        const hasExistingDecision =
          existingDecision === DECISION_APPLY || existingDecision === DECISION_KEEP;

        let keepExistingDecision = false;
        if (hasExistingDecision) {
          if (existingResolvedSignature && currentSentenceSignature) {
            const similarity = calculateSimilarity(existingResolvedSignature, currentSentenceSignature);
            keepExistingDecision = similarity >= MATERIAL_SIMILARITY_THRESHOLD;
          } else {
            keepExistingDecision = true;
          }
        }

        next[key] = {
          userDecision: keepExistingDecision
            ? existingDecision
            : (finding?.userDecision ?? null),
          userJustification: keepExistingDecision
            ? String(existing.userJustification ?? "")
            : String(finding?.userJustification ?? ""),
          resolvedSentenceSignature: keepExistingDecision
            ? (existingResolvedSignature || currentSentenceSignature)
            : "",
          lastSeenSentenceSignature: currentSentenceSignature,
        };
      });
      return next;
    });

    setReductionInputsByIssue((prev) => {
      const next = { ...prev };
      findings.forEach((finding, idx) => {
        if (!requiresEvidenceForIssue(finding)) {
          return;
        }

        const key = getIssueKey(finding, idx);
        next[key] = normalizeEvidenceFields(next[key], finding);
      });
      return next;
    });

    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    analysis,
    CHANGES_REQUIRED_STATUS,
    inputText,
    DECISION_APPLY,
    DECISION_KEEP,
  ]);

  const getRiskColor = (level) => {
    switch ((level || "").toLowerCase()) {
      case "high":
        return "#d97079";
      case "medium":
        return "#c9a06c";
      case "low":
        return "#7aa794";
      default:
        return "#9ca9b3";
    }
  };

  const getFriendlyCategoryLabel = (category) => {
    const key = (category || "").toLowerCase().replace(/_/g, " ");
    if (key.includes("substantiated")) return "Needs evidence";
    if (key.includes("omission")) return "Possible omission";
    if (key.includes("truthful")) return "Claim clarity check";
    if (key.includes("connection")) return "Service connection issue";
    return (category || "Uncategorized").replace(/_/g, " ");
  };

  const getMatchedPatterns = (currentAnalysis) => {
    if (!currentAnalysis?.groupedFindings) return [];
    return Array.from(
      new Set(
        currentAnalysis.groupedFindings
          .filter((finding, idx) => {
            // Remove underlines for issues the user has already resolved with a rewrite
            const key = getIssueKey(finding, idx);
            const dec = issueDecisionsByKey[key];
            return dec?.userDecision !== DECISION_APPLY;
          })
          .flatMap((finding) => finding.matchedPatterns || [])
          .map((pattern) => String(pattern || "").trim())
          .filter(Boolean)
      )
    );
  };

  const renderHighlightedDescription = (text, currentAnalysis) => {
    const content = (text || "").trim();
    if (!content) return <span>Description preview</span>;

    const patterns = getMatchedPatterns(currentAnalysis);
    const sortedPatterns = [...patterns].sort((a, b) => b.length - a.length);
    const regex = patterns.length
      ? new RegExp(`(${sortedPatterns.map(escapeRegex).join("|")})`, "gi")
      : null;

    const highlightLine = (str, keyPrefix) => {
      if (!regex || !str) return str;
      const pieces = str.split(regex);
      return pieces.map((piece, i) => {
        if (!piece) return null;
        const isMatch = sortedPatterns.some((p) => p.toLowerCase() === piece.toLowerCase());
        return isMatch
          ? <mark key={`${keyPrefix}-m${i}`} className="flagged-phrase" title="Flagged by the rules check">{piece}</mark>
          : <React.Fragment key={`${keyPrefix}-t${i}`}>{piece}</React.Fragment>;
      });
    };

    const lines = content.split("\n");
    const elements = [];
    let bulletBuffer = [];
    let key = 0;

    const flushBullets = () => {
      if (!bulletBuffer.length) return;
      elements.push(
        <ul key={`ul-${key++}`} className="preview-bullet-list">
          {bulletBuffer.map((item, i) => (
            <li key={i}>{highlightLine(item, `li-${key}-${i}`)}</li>
          ))}
        </ul>
      );
      bulletBuffer = [];
    };

    for (const line of lines) {
      const trimmed = line.trim();

      if (!trimmed) {
        flushBullets();
        elements.push(<br key={`br-${key++}`} />);
        continue;
      }

      // bullet, starts with - , * , or •
      const bulletMatch = trimmed.match(/^[-*•]\s+(.+)/);
      if (bulletMatch) {
        bulletBuffer.push(bulletMatch[1]);
        continue;
      }

      flushBullets();

      // bold heading, **text** or __text__
      const boldMatch = trimmed.match(/^\*\*(.+?)\*\*$|^__(.+?)__$/);
      if (boldMatch) {
        const label = boldMatch[1] || boldMatch[2];
        elements.push(
          <strong key={`h-${key++}`} className="preview-section-heading">
            {highlightLine(label, `h-${key}`)}
          </strong>
        );
        continue;
      }

      elements.push(
        <span key={`line-${key++}`} className="preview-text-line">
          {highlightLine(trimmed, `line-${key}`)}
        </span>
      );
    }

    flushBullets();
    return <>{elements}</>;
  };

  const handleDocUpload = async (file, claimType, originalClaim) => {
    setUploadingForClaim(claimType);
    try {
      const formData = new FormData();
      formData.append("file", file);
      formData.append("claimType", claimType);
      if (originalClaim) {
        formData.append("originalClaim", originalClaim);
      }

      const res = await fetch(`${API_BASE}/api/extract-doc-facts`, {
        method: "POST",
        headers: await getAuthHeaders(),
        body: formData,
      });

      if (!res.ok) throw new Error("Extraction failed");
      const validation = await res.json();

      // Handle validation response
      if (validation.validationStatus === "VALIDATED") {
        // Auto-fill product facts with validated data
        setProductFacts((prev) => {
          const merged = { ...prev };
          if (validation.materialComposition) {
            merged.materialComposition = validation.materialComposition;
          }
          if (validation.certificationsHeld?.length) {
            const existing = new Set(prev.certificationsHeld);
            validation.certificationsHeld.forEach((c) => existing.add(c));
            merged.certificationsHeld = Array.from(existing);
          }
          if (validation.additionalFacts) {
            merged.additionalFacts = prev.additionalFacts
              ? `${prev.additionalFacts}; ${validation.additionalFacts}`
              : validation.additionalFacts;
          }
          return merged;
        });

        setUploadedForClaims((prev) => ({ ...prev, [claimType]: true }));
        setShowProductFacts(true);

        // Auto-resolve findings that are substantiated by this validated certificate
        const claimKeywords = {
          organic_cotton: ["organic", "gots"],
          recycled_polyester: ["recycled", "grs"],
          recycled_content: ["recycled", "grs"],
          carbon_neutral: ["carbon neutral", "net zero", "carbon"],
          vegan_leather: ["vegan", "leather"],
          sustainable_wool: ["wool", "rws"],
          sustainable_cashmere: ["cashmere", "rws"],
        };
        const keywords = claimKeywords[claimType] || [claimType.replace(/_/g, " ")];
        const currentFindings = analysisRef.current?.groupedFindings || [];
        const keysToResolve = [];
        currentFindings.forEach((finding, idx) => {
          const cat = (finding.category || "").toLowerCase();
          const patterns = (finding.matchedPatterns || []).join(" ").toLowerCase();
          if (keywords.some((kw) => cat.includes(kw) || patterns.includes(kw))) {
            keysToResolve.push(getIssueKey(finding, idx));
          }
        });
        if (keysToResolve.length > 0) {
          setIssueDecisionsByKey((prev) => {
            const next = { ...prev };
            keysToResolve.forEach((key) => {
              if (!prev[key]?.userDecision) {
                next[key] = {
                  ...(prev[key] || {}),
                  userDecision: DECISION_KEEP,
                  userJustification: "Substantiated by uploaded certificate",
                  resolvedSentenceSignature: "",
                };
              }
            });
            return next;
          });
        }

        // Auto-trigger re-analyse with validated facts
        setTimeout(() => {
          analyzeText(inputText, getPrimaryReductionPayload(), false, "manual");
        }, 300);
      } else if (validation.validationStatus === "NEEDS_MORE_INFO") {
        // Show missing fields inline and block auto-fill
        const missingText = validation.requiredFields?.length
          ? validation.requiredFields.join(", ")
          : "Document lacks required information";
        setValidationErrorsByClaim((prev) => ({
          ...prev,
          [claimType]: {
            status: "NEEDS_MORE_INFO",
            feedback: validation.validationFeedback,
            missing: missingText
          }
        }));
      } else if (validation.validationStatus === "CONTRADICTS_CLAIM") {
        // Show contradiction warning inline
        setValidationErrorsByClaim((prev) => ({
          ...prev,
          [claimType]: {
            status: "CONTRADICTS_CLAIM",
            feedback: validation.validationFeedback
          }
        }));
      }
    } catch (err) {
      setValidationErrorsByClaim((prev) => ({
        ...prev,
        [claimType]: {
          status: "READ_ERROR",
          feedback: "Could not read document. Please check the file and try again."
        }
      }));
    } finally {
      setUploadingForClaim(null);
    }
  };

  const handleDescriptionChange = (value) => {
    setInputText(value);
    if (isGuardApproved) {
      setIsGuardApproved(false);
    }
    setOverallStatus(CHANGES_REQUIRED_STATUS);
    setMarkReadyMessage("");
    setIsReviewLocked(false);
  };

  const buildSuggestionForFinding = (finding, sourceText) => {
    const category = String(finding?.category || "").toLowerCase();
    const pattern = String((finding?.matchedPatterns || [])[0] || "").trim();
    const text = String(sourceText || "");
    // stops at a line break or bullet (· or •) too, so ASOS-style copy doesn't get merged into one giant "sentence"
    const firstSentence = (text.match(/[^.!?\n·•]+[.!?]?/)?.[0] || "").trim();

    if (category.includes("reduction") || category.includes("compar")) {
      return "Compared with our previous range, this product uses lower-impact production steps where measured and evidenced.";
    }
    if (category.includes("carbon neutral") || pattern.toLowerCase().includes("carbon neutral") || pattern.toLowerCase().includes("net zero")) {
      return "Any carbon claim should clearly state scope, method and offset details with current evidence.";
    }
    if (pattern && firstSentence && firstSentence.toLowerCase().includes(pattern.toLowerCase())) {
      return firstSentence.replace(new RegExp(pattern, "i"), "a clearly qualified environmental claim");
    }
    return "This environmental claim should be clear, accurate and supported by current evidence.";
  };

  const getIssueDisplayTitle = (finding) => {
    const key = String(finding?.category || "").toLowerCase();
    if (key.includes("reduction") || key.includes("compar")) {
      return "Reduction or comparison claim";
    }
    if (key.includes("truthful") || key.includes("clear") || key.includes("greenwashing")) {
      return "Unclear or broad claim";
    }
    if (key.includes("absolute")) {
      return "Absolute claim";
    }
    return getFriendlyCategoryLabel(finding?.category);
  };

  const getIssueSummaryText = (finding) => {
    const fromApi = String(finding?.issueSummary || "").trim();
    if (fromApi) return fromApi;
    const key = String(finding?.category || "").toLowerCase();
    if (key.includes("reduction") || key.includes("compar")) {
      return "State what is being reduced, from what baseline, by how much and over what period.";
    }
    if (key.includes("truthful") || key.includes("clear") || key.includes("greenwashing")) {
      return "Terms like 'eco-friendly' or 'sustainable' need clear meaning and evidence.";
    }
    return getSingleSentence(String(finding?.explanation || "").trim())
      || "Review this claim for accuracy and evidence.";
  };

  // matches the backend's EngineStatus values (Program.cs / AnalyzeResponse.AiStatus). "Skipped" (a rules-only check on purpose) is handled separately and never reaches here
  const describeAiUnavailable = (aiStatus) => {
    switch (aiStatus) {
      case "Timeout":
        return "AI rewrite unavailable (it took too long to respond), so rule guidance is shown instead.";
      case "NotConfigured":
        return "AI rewrite unavailable (no AI key is set up), so rule guidance is shown instead.";
      case "Failed":
        return "AI rewrite unavailable (the AI check failed), so rule guidance is shown instead.";
      default:
        return "AI rewrite unavailable right now, so rule guidance is shown instead.";
    }
  };

  const getSuggestionTextForIssue = (finding) => {
    const fromApi = getCleanRewrite(String(finding?.suggestionText || "").trim());
    if (fromApi) return fromApi;
    const reductionApi = getCleanRewrite(String(finding?.suggestedReductionSentence || "").trim());
    if (reductionApi) return reductionApi;
    return getCleanRewrite(buildSuggestionForFinding(finding, inputText));
  };

  const getRegulationAlertsForFinding = (finding) => {
    const category = String(finding?.category || "").toLowerCase();
    const matched = String((finding?.matchedPatterns || [])[0] || "").toLowerCase();
    return PINNED_REGULATION_UPDATES.filter((u) =>
      u.affectedClaimTypes.some((t) =>
        category.includes(t.toLowerCase()) || matched.includes(t.toLowerCase())
      )
    );
  };

  const handleApplyDecision = (finding, idx) => {
    const issueKey = getIssueKey(finding, idx);
    if (isReviewLocked) return;

    // use the same single sentence shown to the user, the full multi-line block would add duplicate lines to the description
    const suggestion = getSuggestionTextForIssue(finding);
    if (suggestion) {
      const resolvedSentenceSignature = normalizeForSimilarity(
        getSentenceForFinding(inputText, finding)
      );
      applySuggestion(suggestion, null, issueKey, finding, resolvedSentenceSignature);
    } else {
      const resolvedSentenceSignature = normalizeForSimilarity(
        getSentenceForFinding(inputText, finding)
      );
      updateIssueDecision(issueKey, {
        userDecision: DECISION_APPLY,
        userJustification: "",
        resolvedSentenceSignature,
      });
      setOverallStatus(CHANGES_REQUIRED_STATUS);
      setMarkReadyMessage("");
      setIsReviewLocked(false);
    }
  };


  const findings = analysis?.groupedFindings || [];
  // issueStates / issueStateByKey now come from useIssueDecisions.

  const rawComplianceScore = analysis?.complianceScore ?? 0;
  const hasDescriptionContent = Boolean(normalizedInputText);
  const canShowComplianceScore = hasDescriptionContent && Boolean(analysis);
  const unresolvedHighCount = issueStates.filter((issueState) =>
    issueState.isHighSeverity && !issueState.highResolved
  ).length;
  const unresolvedMediumCount = issueStates.filter((issueState) =>
    issueState.isMediumSeverity && !issueState.decisionComplete
  ).length;
  const unresolvedAnyCount = issueStates.filter((issueState) => !issueState.decisionComplete).length;
  const hasKeptMediumOrLow = issueStates.some((issueState) =>
    !issueState.isHighSeverity
    && issueState.userDecision === DECISION_KEEP
    && issueState.decisionComplete
  );
  const publishGateSatisfied =
    hasDescriptionContent
    && unresolvedHighCount === 0
    && issueStates.every((issueState) => issueState.decisionComplete);
  const showReadyBanner = isGuardApproved && !textChangedSinceApproval && !hasKeptMediumOrLow;
  const showProceedWithCareBanner = isGuardApproved && !textChangedSinceApproval && hasKeptMediumOrLow;
  const canPublish = publishGateSatisfied && !textChangedSinceApproval;
  // said out loud next to the Submit button, not just a title tooltip, a disabled button gives no feedback on click so the reason needs to be visible before that
  const submitDisabledReason = !hasDescriptionContent
    ? "Write the product description first."
    : !analysis
      ? "Still checking the description. Try again in a moment."
      : unresolvedHighCount > 0
      ? "Resolve the critical issue(s) before this can be submitted."
      : unresolvedAnyCount > 0
        ? "Decide on the remaining issue(s) before this can be submitted."
        : textChangedSinceApproval
          ? "The text changed since it was last checked. Checking again."
          : null;
  const canPublishProduct = hasPermission(user, "publish:product");
  // Two things a Senior Editor may only do with a permission of their own.
  const canOverride = hasPermission(user, "override:compliance");
  const canEditFacts = hasPermission(user, "edit:product-facts");
  const saveLine =
    myProduct.save.state === "saving"
      ? "Saving..."
      : myProduct.save.state === "error"
        ? `Not saved: ${myProduct.save.message}`
        : myProduct.dirty
          ? "Unsaved changes"
          : myProduct.save.state === "saved" && myProduct.save.at
            ? `Draft saved at ${new Date(myProduct.save.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`
            : "";
  const canSignOff = canPublish && canPublishProduct;

  const findingsWithMeta = findings.map((finding, idx) => ({
    finding,
    idx,
    issueKey: getIssueKey(finding, idx),
  }));

  // one at a time, always works on the first unresolved issue
  const unresolvedIssues = findingsWithMeta.filter(
    (item) => !issueStateByKey[item.issueKey]?.decisionComplete
  );
  const resolvedCount = findingsWithMeta.length - unresolvedIssues.length;
  const currentIssue = unresolvedIssues[0] || null;

  const allResolved = findingsWithMeta.length > 0 &&
    findingsWithMeta.every((item) => issueStateByKey[item.issueKey]?.decisionComplete);

  // decision based compliance score, starts from the backend's honest rawComplianceScore and adds back each resolved issue's weight (full credit for APPLY, half for KEEP), reflects actual risk remaining, no gamification or re-scanning loop
  const SEVERITY_WEIGHTS = { high: 12, medium: 7, low: 4 };
  const effectiveComplianceScore = (() => {
    if (!analysis || findingsWithMeta.length === 0) return rawComplianceScore;
    let bonus = 0;
    for (const { finding, issueKey } of findingsWithMeta) {
      const weight = SEVERITY_WEIGHTS[finding.severity?.toLowerCase()] ?? 4;
      const state = issueStateByKey[issueKey];
      if (state?.decisionComplete && state.userDecision === DECISION_APPLY) {
        bonus += weight;
      } else if (state?.decisionComplete && state.userDecision === DECISION_KEEP) {
        bonus += Math.round(weight * 0.5);
      }
    }
    return Math.min(100, rawComplianceScore + bonus);
  })();

  const effectiveRiskLevel = (() => {
    if (!allResolved) return analysis?.overallRisk ?? "Unknown";
    const anyKept = issueStates.some((s) => s.userDecision === DECISION_KEEP && s.decisionComplete);
    return anyKept ? "Medium" : "Low";
  })();

  const summaryStatusText = showProceedWithCareBanner
    ? "Proceed with care (internal legal review required)"
    : unresolvedHighCount > 0
      ? "Changes required - high risk issue(s) open"
      : unresolvedMediumCount > 0 || unresolvedAnyCount > 0
        ? "Changes required"
        // only renders once analysis exists (canShowComplianceScore needs it), so nothing unresolved means nothing left to say
        : "";

  // When every issue is addressed, either validate via backend (evidence path) or auto-approve (apply path).
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!allResolved || !publishGateSatisfied || isGuardApproved) return;

    if (isValidatingEvidenceRef.current) {
      // evidence path, one backend call so the LLM can check the evidence, isGuardApproved then turns true via the overallStatus effect so there's no loop
      isValidatingEvidenceRef.current = false;
      const timer = setTimeout(() => {
        analyzeTextRef.current(inputText, null, false, "manual");
      }, 300);
      return () => clearTimeout(timer);
    }

    // apply suggestion path, the AI rewrite already makes the copy compliant, so approve it client side
    setIsGuardApproved(true);
    setLastApprovedText(normalizedInputText);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [allResolved, publishGateSatisfied]);

  const isOverClaimedImpactIssue = (finding) => {
    const key = String(finding?.category || "").toLowerCase();
    return key.includes("absolute")
      || key.includes("carbon neutral")
      || key.includes("substantiated")
      || key.includes("omission");
  };

  const isBuzzwordIssue = (finding) => {
    const key = String(finding?.category || "").toLowerCase();
    return key.includes("truthful")
      || key.includes("clear")
      || key.includes("greenwashing")
      || key.includes("vague");
  };

  const isReductionOrRecycledIssue = (finding) => {
    const key = String(finding?.category || "").toLowerCase();
    const patterns = Array.isArray(finding?.matchedPatterns)
      ? finding.matchedPatterns.map((pattern) => String(pattern || "").toLowerCase())
      : [];
    return key.includes("reduction")
      || key.includes("compar")
      || key.includes("recycled")
      || patterns.some((pattern) => pattern.includes("recycled"));
  };

  const keyIssueDefinitions = [
    {
      key: "over-claimed-impact",
      title: "Over-claimed impact",
      matcher: isOverClaimedImpactIssue,
      defaultRisk:
        "Absolute climate or impact claims can mislead if they are not fully qualified and clearly evidenced.",
    },
    {
      key: "vague-green-buzzwords",
      title: "Vague green buzzwords",
      matcher: isBuzzwordIssue,
      defaultRisk:
        "Broad terms like eco-friendly or sustainable need clear meaning in the copy to avoid misleading shoppers.",
    },
    {
      key: "reduction-recycled-claims",
      title: "Reduction & recycled claims",
      matcher: isReductionOrRecycledIssue,
      defaultRisk:
        "Reduction and recycled-content claims should be specific about what changed and how that change is measured.",
    },
  ];

  const keyIssueCards = keyIssueDefinitions
    .map((definition) => {
      const items = findingsWithMeta.filter(({ finding, issueKey }) => {
        if (!definition.matcher(finding)) return false;
        const issueState = issueStateByKey[issueKey] || {};
        if (issueState.decisionComplete) return false;

        const hasRiskyLine = Boolean(getIssueSummaryText(finding));
        const hasSuggestion = Boolean(getSuggestionTextForIssue(finding));
        return hasRiskyLine || hasSuggestion;
      });
      if (!items.length) return null;

      const primaryItem = items[0];
      const riskyLine = getIssueSummaryText(primaryItem.finding) || definition.defaultRisk;
      const suggestionText = getSuggestionTextForIssue(primaryItem.finding);

      return {
        ...definition,
        items,
        primaryItem,
        riskyLine,
        suggestionText,
      };
    })
    .filter(Boolean)
    .slice(0, 3);



  const handleKeepDecision = (issueKey, justification) => {
    const item = findingsWithMeta.find((entry) => entry.issueKey === issueKey);
    const fallbackSignature = item
      ? normalizeForSimilarity(getSentenceForFinding(inputText, item.finding))
      : "";
    updateIssueDecision(issueKey, {
      userDecision: DECISION_KEEP,
      userJustification: String(justification || ""),
      resolvedSentenceSignature: fallbackSignature,
    });
    setIsGuardApproved(false);
    setLastApprovedText("");
    setOverallStatus(CHANGES_REQUIRED_STATUS);
    setMarkReadyMessage("");
    setIsReviewLocked(false);
  };


  // returns the server-side product id, creates the product on first use. two products can share a name, only the id identifies one
  const ensureProduct = async () => {
    if (!String(productName || "").trim()) {
      setMarkReadyMessage("Give the product a name before submitting it for review.");
      return null;
    }

    try {
      return await myProduct.actions.ensureProduct();
    } catch (error) {
      setMarkReadyMessage(error?.message || "Unable to create the product.");
      return null;
    }
  };

  // shared by both roles, validates and saves a compliance_reviews row via mark-ready. Copywriters stop here (saved, awaiting sign-off), Senior Editors go one step further and call /api/claims/publish right after
  const submitForReview = async () => {
    if (!canPublish || !analysis) {
      return { ok: false };
    }

    const productId = await ensureProduct();
    if (!productId) {
      return { ok: false };
    }

    const issueDecisions = findingsWithMeta.map(({ finding, issueKey }) => {
      const issueState = issueStateByKey[issueKey] || {};
      const evidenceFields = normalizeEvidenceFields(reductionInputsByIssue[issueKey], finding);

      return {
        issueId: finding?.issueId || issueKey,
        severity: String(finding?.severity || "").toUpperCase(),
        userDecision: issueState.userDecision || null,
        userJustification: issueState.userJustification || null,
        requiresEvidence: Boolean(issueState.requiresEvidence),
        evidenceFields: issueState.requiresEvidence ? toEvidencePayload(evidenceFields) : null,
      };
    });

    setMarkReadyMessage("");
    setIsReviewLocked(false);

    try {
      const response = await fetch(`${API_BASE}/api/claims/mark-ready`, {
        method: "POST",
        headers: await getAuthHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify({
          productId,
          market,
          finalDescription: normalizedInputText,
          category: primaryCategory.trim() || null,
          subcategory: secondaryCategory.trim() || null,
          tags: productTags.trim() || null,
          issueDecisions,
          productFacts: {
            materialComposition: productFacts.materialComposition.trim() || null,
            certificationsHeld: productFacts.certificationsHeld.length ? productFacts.certificationsHeld : null,
            additionalFacts: productFacts.additionalFacts.trim() || null,
          },
        }),
      });

      const result = await response.json();
      if (!response.ok) {
        throw new Error(result?.message || "Unable to mark as ready");
      }

      setOverallStatus(result?.overallStatus || CHANGES_REQUIRED_STATUS);
      // The server re-checks the copy itself and lists exactly what is still blocking submission.
      setMarkReadyMessage([result?.message, ...(result?.validationErrors || [])].filter(Boolean).join(" "));

      if (result?.overallStatus === READY_TO_PUBLISH_STATUS) {
        return { ok: true, productId, message: result?.message };
      }

      setIsGuardApproved(false);
      setLastApprovedText("");
      return { ok: false };
    } catch (error) {
      setOverallStatus(CHANGES_REQUIRED_STATUS);
      setIsGuardApproved(false);
      setLastApprovedText("");
      setIsReviewLocked(false);
      setMarkReadyMessage(error?.message || "Unable to validate publish readiness.");
      return { ok: false };
    }
  };

  // Back to an empty form, and forget which product it was.
  const clearProductForm = () => {
    setProductName("");
    setInputText("");
    setMarket("UK");
    setPrimaryCategory("");
    setSecondaryCategory("");
    setProductTags("");
    setAnalysis(null);
    setIsGuardApproved(false);
    setLastApprovedText("");
    setOverallStatus(CHANGES_REQUIRED_STATUS);
    setIssueDecisionsByKey({});
    setKeepJustPendingByKey({});
    setKeepJustTextByKey({});
    setReductionInputsByIssue({});
    setMarkReadyMessage("");
    setIsReviewLocked(false);
    setSubmitAttempted(false);
    lastAiKeyRef.current = "";
    myProduct.actions.reset();
  };

  const resetProductForm = () => {
    clearProductForm();
    setActiveSidebarSubItem(isCopywriter ? "my-products" : "product-lists");
  };

  // Puts what the server sent back into the form, as if it had just been opened.
  const applyLoadedProduct = (loaded) => {
    setProductName(loaded.name);
    setInputText(loaded.text);
    setMarket(loaded.market);
    setAnalysis(null);
    setIsGuardApproved(false);
    setLastApprovedText("");
    setOverallStatus(CHANGES_REQUIRED_STATUS);
    setIssueDecisionsByKey({});
    setKeepJustPendingByKey({});
    setKeepJustTextByKey({});
    setReductionInputsByIssue({});
    setMarkReadyMessage("");
    setIsReviewLocked(false);
    setSubmitAttempted(false);
    lastAiKeyRef.current = "";
  };

  // opens one of the person's own products in the editor, a draft or sent-back one to edit, or one in review or published to read
  const openProduct = async (id) => {
    setActiveSidebarItem("product");
    setActiveSidebarSubItem("add-product");
    const loaded = await myProduct.actions.open(id);
    if (loaded) applyLoadedProduct(loaded);
  };

  const handleWithdraw = async () => {
    setIsWithdrawing(true);
    const loaded = await myProduct.actions.withdraw();
    setIsWithdrawing(false);
    if (loaded) {
      applyLoadedProduct(loaded);
      reloadMyWork();
    }
  };

  // throws away the saved draft, after asking first, since it can't be brought back
  const handleDiscard = async () => {
    if (currentProductId && !window.confirm("Discard this draft? Your saved text is deleted and cannot be brought back.")) return;
    if (!(await myProduct.actions.discard())) return;
    clearProductForm();
    reloadMyWork();
  };

  // Same action, from the My Products list, for a product that may not be the one open in the editor.
  const handleDeleteDraftFromList = async (productId) => {
    if (!window.confirm("Discard this draft? Your saved text is deleted and cannot be brought back.")) return;
    try {
      await api.deleteDraft(productId);
    } catch (err) {
      window.alert(err.message || "Could not discard the draft.");
      return;
    }
    if (currentProductId === productId) clearProductForm();
    reloadMyWork();
  };

  // leaving the description is a natural moment to ask the AI, once per text, not when heading to a button (Analyse, Submit) since that asks for it already
  const handleDescriptionBlur = (event) => {
    if (!myProduct.editable) return;
    if (event?.relatedTarget?.closest?.("button")) return;
    const text = String(inputText || "").trim();
    if (text.length <= 10) return;
    const key = `${market}\n${text}`;
    if (lastAiKeyRef.current === key) return;
    lastAiKeyRef.current = key;
    analyzeText(inputText, getPrimaryReductionPayload(), false, "manual");
  };

  // Copywriter action, saves the completed review for sign-off but doesn't publish. fixes the old bug where a Copywriter's work lived only in React state and vanished if they navigated away before a Senior Editor was around
  const handleSubmitForReview = async () => {
    const result = await submitForReview();
    if (!result.ok) return;

    // now it's in review, show it read only with Withdraw, and say what happened
    const loaded = await myProduct.actions.refresh();
    if (loaded) applyLoadedProduct(loaded);
    myProduct.actions.notify("ok", result.message || "Submitted for review.");
    reloadMyWork();
  };

  // Senior Editor action, submits and publishes in one click so an editor working solo stays one step, copywriter-submitted items still go through Pending Review's separate Publish action
  const handleAddProduct = async () => {
    const submitResult = await submitForReview();
    if (!submitResult.ok) return;

    try {
      const publishResponse = await fetch(`${API_BASE}/api/claims/publish`, {
        method: "POST",
        headers: await getAuthHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify({ productId: submitResult.productId }),
      });
      const publishResult = await publishResponse.json();
      if (!publishResponse.ok || !publishResult?.success) {
        throw new Error([publishResult?.message || "Unable to publish", ...(publishResult?.blockingReasons || [])].join(" "));
      }

      setIsGuardApproved(true);
      setLastApprovedText(normalizedInputText);
      setIsReviewLocked(true);
      window.alert(`${productName || "Product"} published.`);
      resetProductForm();
    } catch (error) {
      setMarkReadyMessage(error?.message || "Unable to publish.");
    }
  };

  useEffect(() => {
    const isReadyFromBackend = overallStatus === READY_TO_PUBLISH_STATUS;

    if (isReadyFromBackend && normalizedInputText && !textChangedSinceApproval) {
      if (!isGuardApproved) {
        setIsGuardApproved(true);
        setLastApprovedText(normalizedInputText);
      }
      return;
    }

    if (isGuardApproved && textChangedSinceApproval) {
      setIsGuardApproved(false);
    }
  }, [
    overallStatus,
    READY_TO_PUBLISH_STATUS,
    isGuardApproved,
    normalizedInputText,
    textChangedSinceApproval,
  ]);

  const exampleTexts = [
    {
      name: "Organic Cotton Tee",
      copy: "Our 100% eco-friendly organic cotton tee is completely sustainable and carbon neutral.",
    },
    {
      name: "Activewear Leggings",
      copy: "Made with responsibly sourced recycled fibers and verified low-impact dye process.",
    },
    {
      name: "Midi Dress (bullet style)",
      copy: "Round neck · Long sleeves · Regular fit · Made with organic cotton for a sustainable choice · Recycled polyester lining · Machine wash according to label",
    },
  ];

  // Data Transfer is gated on publish:product (like a real write op) but shown to both roles, dimmed with a lock for a Copywriter instead of hidden. the backend enforces the same permission on its own (RequirePublishPermission), so this only hides the entry point, never the real boundary
  const mainNavItems = [
    { id: "home", label: "Overview", icon: LayoutDashboard },
    { id: "product", label: "Product", icon: Boxes },
    { id: "products-footer", label: "Product Inventory", icon: Boxes },
    { id: "reports", label: "Reports", icon: ExternalLink },
    { id: "data-transfer", label: "Data Transfer", icon: Lightbulb, requiresPublish: true },
    { id: "pending-review", label: "Pending Review", icon: CheckCircle },
    { id: "verified-facts", label: "Verified Facts", icon: ShieldCheck, requiresPublish: true },
    { id: "audit-trail", label: "Audit Trail", icon: ClipboardList, requiresPublish: true },
  ];

  const productSubItems = isCopywriter
    ? [
        { id: "add-product", label: "Add Product" },
        { id: "my-products", label: "My Products" },
      ]
    : [
        { id: "add-product", label: "Add Product" },
        { id: "product-lists", label: "Product Lists" },
        { id: "products-sub", label: "Products" },
      ];

  const utilityNavItems = [
    { id: "help", label: "Help", icon: AlertTriangle },
    { id: "account-settings", label: "Account Settings", icon: Settings },
    { id: "activity", label: "Activity", icon: CheckCircle },
    { id: "exit", label: "Sign Out", icon: ExternalLink },
  ];

  return (
    <div className="shell plugin-shell">
      <aside className="sidebar">
        <div className="sidebar-inner">
          <div className="sidebar-header">
            <div className="sidebar-brand-mark">
              <span className="sidebar-brand-glyph" />
            </div>
            <div className="sidebar-brand-copy">
              <h1 className="sidebar-title">GREENCLAIMS</h1>
            </div>
          </div>

          <nav className="sidebar-nav">
            {mainNavItems.map((item, index) => {
              const Icon = item.icon;
              const isActive = activeSidebarItem === item.id;
              const locked = Boolean(item.requiresPublish) && !canPublishProduct;
              const groupLabel = index === 0 ? "Main" : null;

              if (item.id === "product") {
                return (
                  <div key={item.id} className="nav-group">
                    <button
                      type="button"
                      className={`nav-item ${isActive ? "nav-item--active active" : ""}`}
                      onClick={() => setActiveSidebarItem(item.id)}
                    >
                      <Icon size={16} />
                      {item.label}
                    </button>
                    <div className="sub-nav">
                      {productSubItems.map((subItem) => (
                        <button
                          key={subItem.id}
                          type="button"
                          className={`sub-nav-item ${activeSidebarSubItem === subItem.id ? "active" : ""}`}
                          onClick={() => {
                            setActiveSidebarItem("product");
                            setActiveSidebarSubItem(subItem.id);
                          }}
                        >
                          {subItem.label}
                        </button>
                      ))}
                    </div>
                  </div>
                );
              }
              return (
                <React.Fragment key={item.id}>
                  {groupLabel && <p className="sidebar-footer-title nav-group-title">{groupLabel}</p>}
                  <button
                    type="button"
                    className={`nav-item ${isActive ? "nav-item--active active" : ""} ${locked ? "nav-item--locked" : ""}`}
                    onClick={() => (locked ? window.alert("Senior Editor only.") : setActiveSidebarItem(item.id))}
                  >
                    <Icon size={16} />
                    {item.label}
                    {locked && <Lock size={12} className="nav-item-lock" />}
                  </button>
                </React.Fragment>
              );
            })}
          </nav>

          <div className="sidebar-footer">
            <button
              type="button"
              className="nav-item sidebar-theme-toggle"
              onClick={toggleTheme}
              aria-label={theme === "dark" ? "Switch to light mode" : "Switch to dark mode"}
            >
              {theme === "dark" ? <Sun size={16} /> : <Moon size={16} />}
              {theme === "dark" ? "Light mode" : "Dark mode"}
            </button>
            <p className="sidebar-footer-title">Account</p>
            {utilityNavItems.map((item) => {
              const Icon = item.icon;
              return (
                <button
                  key={item.id}
                  type="button"
                  className={`nav-item ${activeSidebarItem === item.id ? "nav-item--active active" : ""}`}
                  onClick={() => setActiveSidebarItem(item.id)}
                >
                  <Icon size={16} />
                  {item.label}
                </button>
              );
            })}
          </div>
        </div>
      </aside>

      <div className="main workspace">
        <div className={`workspace-chrome ${isChromeScrolled ? "is-scrolled" : ""}`}>
          <header className={`topbar glass-panel utility-topbar combined-topbar ${isChromeScrolled ? "is-scrolled" : ""}`}>
            <div className="topbar-head-row">
              <div className="topbar-right">
                <div className="topbar-action-wrapper">
                  <button
                    type="button"
                    className={`admin-icon-btn ${activeTopbarMenu === "bell" ? "active" : ""}`}
                    aria-label="Notifications"
                    onClick={() => setActiveTopbarMenu(activeTopbarMenu === "bell" ? null : "bell")}
                  >
                    <Bell size={16} />
                    <span className="notification-badge" />
                  </button>
                  {activeTopbarMenu === "bell" && (
                    <div className="topbar-popover glass-panel">
                      <div className="popover-header">Notifications</div>
                      <div className="popover-content">
                        <div className="popover-empty">No new notifications.</div>
                      </div>
                    </div>
                  )}
                </div>

                <div className="topbar-action-wrapper">
                  <button
                    type="button"
                    className={`admin-icon-btn ${activeTopbarMenu === "mail" ? "active" : ""}`}
                    aria-label="Messages"
                    onClick={() => setActiveTopbarMenu(activeTopbarMenu === "mail" ? null : "mail")}
                  >
                    <Mail size={16} />
                  </button>
                  {activeTopbarMenu === "mail" && (
                    <div className="topbar-popover glass-panel">
                      <div className="popover-header">Messages</div>
                      <div className="popover-content">
                        <div className="popover-empty">No new messages.</div>
                      </div>
                    </div>
                  )}
                </div>

                <div className="topbar-action-wrapper">
                  <div
                    className={`admin-user compact ${activeTopbarMenu === "user" ? "active" : ""}`}
                    style={{ cursor: "pointer" }}
                    onClick={() => setActiveTopbarMenu(activeTopbarMenu === "user" ? null : "user")}
                  >
                    <span className="admin-avatar">
                      <UserCircle size={28} strokeWidth={1.5} color="#6b7280" />
                    </span>
                    <div className="admin-meta">
                      <strong>{user?.email?.split("@")[0] || "Staff"}</strong>
                      <span>{getPersona(user).label}</span>
                    </div>
                  </div>
                  {activeTopbarMenu === "user" && (
                    <div className="topbar-popover glass-panel user-menu">
                      <button type="button" className="menu-item" onClick={() => { setActiveSidebarItem("account-settings"); setActiveTopbarMenu(null); }}>
                        <Settings size={14} /> My Account
                      </button>
                      <button type="button" className="menu-item" onClick={() => { setActiveSidebarItem("activity"); setActiveTopbarMenu(null); }}>
                        <CheckCircle size={14} /> Activity Log
                      </button>
                      <hr />
                      <button type="button" className="menu-item text-danger" onClick={() => { setActiveSidebarItem("exit"); setActiveTopbarMenu(null); }}>
                        <ExternalLink size={14} /> Sign out
                      </button>
                    </div>
                  )}
                </div>
              </div>
            </div>
            <div className="topbar-title-row">
              <p className="breadcrumb-line">
                <span>OVERVIEW</span> / <span>{(activeSidebarItem === "overview" || activeSidebarItem === "home") ? "DASHBOARD" : activeSidebarItem.replace("-", " ").toUpperCase()}</span> {activeSidebarItem === "product" && (
                  <> / <span className="breadcrumb-current">{activeSidebarSubItem.replace("-", " ").toUpperCase()}</span></>
                )}
              </p>
              <h2 className="page-title">
                {activeSidebarItem === "product"
                  ? activeSidebarSubItem === "add-product"
                    ? "New Product"
                    : activeSidebarSubItem === "product-lists"
                      ? "Product Lists"
                      : "Products"
                  : (activeSidebarItem === "home" || activeSidebarItem === "overview")
                    ? "Dashboard Overview"
                    : activeSidebarItem === "data-transfer"
                      ? "Data Transfer"
                      : mainNavItems.find(i => i.id === activeSidebarItem)?.label || utilityNavItems.find(i => i.id === activeSidebarItem)?.label || "Page"}
              </h2>
            </div>
          </header>
        </div>
        <main className="main-content">
          {activeSidebarItem === "product" && activeSidebarSubItem === "add-product" ? (
            <div className="content-grid">
              <div className="column column-left plugin-panel-column">
                <section className="glass-panel card preview-card">
                  <div className="preview-header">
                    <h2 className="card-title">Product page preview</h2>
                    <div className="preview-header-right">
                      {canShowComplianceScore && (
                        <span className={`preview-status ${String(effectiveRiskLevel || "unknown").toLowerCase()}`}>
                          Score {effectiveComplianceScore}% - Risk{" "}
                          {String(effectiveRiskLevel || "unknown").toLowerCase()}
                        </span>
                      )}
                    </div>
                  </div>

                  <div className="preview-image">
                    {/* Product photos aren't a feature of this app yet: no stand-in picture of someone else's product is ever shown here. */}
                    <span className="preview-image-empty">No photo on file</span>
                  </div>

                  <div className="preview-content">
                    <h3 className="preview-product-name">{productName.trim() || "Product title preview"}</h3>
                    <div className="preview-tags">
                      {Array.from(
                        new Set(
                          [primaryCategory, secondaryCategory, ...productTags.split(",").map((tag) => tag.trim())].filter(
                            Boolean
                          )
                        )
                      ).map((tag, idx) => (
                        <span key={`${tag}-${idx}`} className="preview-tag">
                          {tag}
                        </span>
                      ))}
                    </div>
                    <div className="preview-live-description">{renderHighlightedDescription(inputText, analysis)}</div>
                  </div>
                  <div className="preview-actions">
                    {canPublishProduct ? (
                      <button type="button" className="btn-primary" onClick={handleAddProduct} disabled={!canSignOff || !myProduct.editable}>
                        Publish
                      </button>
                    ) : (
                      <button
                        type="button"
                        className="btn-primary"
                        onClick={() => {
                          setSubmitAttempted(true);
                          if (canPublish) handleSubmitForReview();
                        }}
                        disabled={!myProduct.editable}
                        aria-describedby={submitAttempted && submitDisabledReason && myProduct.editable ? "submit-disabled-reason" : undefined}
                        title={canPublish ? "Ready for sign-off" : "Resolve all findings before submitting."}
                      >
                        Submit
                      </button>
                    )}
                  </div>
                  {!canPublishProduct && submitAttempted && submitDisabledReason && myProduct.editable && (
                    <p id="submit-disabled-reason" className="submit-block-reason" role="alert">
                      {submitDisabledReason}
                    </p>
                  )}
                </section>
              </div>

              <div className="column column-right plugin-panel-column">
                <section className="glass-panel card editor-card">
                  <div className="tabs tab-row">
                    {["description", "examples", "guidance"].map((tab) => (
                      <button
                        key={tab}
                        type="button"
                        onClick={() => setActiveTab(tab)}
                        className={`tab tab-btn ${activeTab === tab ? "tab-active active" : ""}`}
                      >
                        {tab}
                      </button>
                    ))}
                  </div>

                  {activeTab === "description" && (
                    <>
                      <ProductStatusBanners
                        status={myProduct.status}
                        sendBack={myProduct.sendBack}
                        notice={myProduct.notice}
                        busy={isWithdrawing}
                        onWithdraw={handleWithdraw}
                        onStartNew={clearProductForm}
                      />
                      <div className="field-group">
                        <label className="field-label" htmlFor="product-name">Product Name</label>
                        <input
                          id="product-name"
                          value={productName}
                          onChange={(e) => setProductName(e.target.value)}
                          className="field-input"
                          readOnly={!myProduct.editable}
                        />
                        <label className="field-label" htmlFor="product-market">Market</label>
                        <select
                          id="product-market"
                          className="field-input"
                          value={market}
                          onChange={(e) => setMarket(e.target.value)}
                          disabled={!myProduct.editable}
                        >
                          <option value="UK">UK (CMA Green Claims Code, CAP Code)</option>
                          <option value="EU">EU (Empowering Consumers Directive 2024/825)</option>
                        </select>
                        <label className="field-label" htmlFor="product-description">Product Description</label>
                        <textarea
                          id="product-description"
                          value={inputText}
                          onBlur={handleDescriptionBlur}
                          onChange={(e) => handleDescriptionChange(e.target.value)}
                          className={`field-textarea${publishGateSatisfied && findingsWithMeta.length > 0 ? " field-textarea--locked" : ""}`}
                          readOnly={(publishGateSatisfied && findingsWithMeta.length > 0) || !myProduct.editable}
                        />
                        {publishGateSatisfied && findingsWithMeta.length > 0 && (
                          <div className="textarea-lock-notice">
                            <span>Description locked for sign-off.</span>
                            <button
                              type="button"
                              className="reset-review-btn"
                              onClick={() => {
                                setIssueDecisionsByKey({});
                                setKeepJustPendingByKey({});
                                setKeepJustTextByKey({});
                                setReductionInputsByIssue({});
                                setIsGuardApproved(false);
                                setLastApprovedText("");
                                setOverallStatus(CHANGES_REQUIRED_STATUS);
                                setMarkReadyMessage("");
                                setIsReviewLocked(false);
                              }}
                            >
                              Reset review
                            </button>
                          </div>
                        )}
                      </div>

                      <VerifiedFactsPanel
                        facts={myProduct.facts}
                        factsRequest={myProduct.factsRequest}
                        canRequest={myProduct.editable}
                        onRequest={myProduct.actions.requestFacts}
                      />

                      {/* Product facts panel */}
                      {analysis && (
                        <div className="pf-panel">
                          <button type="button" className="pf-toggle" onClick={() => setShowProductFacts((v) => !v)}>
                            <span className="pf-toggle-label">
                              Substantiate your claims
                              {analysis?.requiredDocuments?.length > 0 && (
                                <span className="pf-badge">{analysis.requiredDocuments.length - Object.keys(uploadedForClaims).length > 0 ? analysis.requiredDocuments.length - Object.keys(uploadedForClaims).length : "All verified"}</span>
                              )}
                            </span>
                            <span className="pf-toggle-hint">{showProductFacts ? "Hide ▲" : "Show ▼"}</span>
                          </button>

                          {showProductFacts && (
                            <div className="pf-body">
                              {analysis?.requiredDocuments?.length > 0 ? (
                                <>
                                  <div className="pf-doc-list">
                                    {analysis.requiredDocuments.map((doc) => (
                                      <div key={doc.claimType}>
                                        <div className="pf-doc-row">
                                          <span className="pf-doc-name">{doc.documentName}</span>
                                          {uploadedForClaims[doc.claimType] ? (
                                            <span className="pf-doc-done">Verified</span>
                                          ) : uploadingForClaim === doc.claimType ? (
                                            <span className="pf-doc-loading">Reading…</span>
                                          ) : (
                                            <label className="pf-upload-btn">
                                              Upload certificate
                                              <input type="file" accept=".pdf,.txt" style={{ display: "none" }}
                                                onChange={(e) => {
                                                  const file = e.target.files?.[0];
                                                  if (file) handleDocUpload(file, doc.claimType);
                                                  e.target.value = "";
                                                }}
                                              />
                                            </label>
                                          )}
                                        </div>
                                        {validationErrorsByClaim[doc.claimType] && (
                                          <div className={`pf-error pf-error-${validationErrorsByClaim[doc.claimType].status.toLowerCase()}`}>
                                            {validationErrorsByClaim[doc.claimType].status === "NEEDS_MORE_INFO" ? (
                                              <>
                                                <strong>Missing information</strong>
                                                <p>{validationErrorsByClaim[doc.claimType].feedback}</p>
                                                <p><strong>Required:</strong> {validationErrorsByClaim[doc.claimType].missing}</p>
                                              </>
                                            ) : validationErrorsByClaim[doc.claimType].status === "CONTRADICTS_CLAIM" ? (
                                              <>
                                                <strong>Document contradicts claim</strong>
                                                <p>{validationErrorsByClaim[doc.claimType].feedback}</p>
                                              </>
                                            ) : validationErrorsByClaim[doc.claimType].status === "READ_ERROR" ? (
                                              <>
                                                <strong>Could not read file</strong>
                                                <p>{validationErrorsByClaim[doc.claimType].feedback}</p>
                                              </>
                                            ) : null}
                                          </div>
                                        )}
                                      </div>
                                    ))}
                                  </div>
                                  <button type="button" className="pf-manual-toggle" onClick={() => setShowManualFacts((v) => !v)}>
                                    {showManualFacts ? "Hide manual fields ▲" : "Enter facts manually instead ▼"}
                                  </button>
                                </>
                              ) : null}

                              {(showManualFacts || !analysis?.requiredDocuments?.length) && (
                                <div className="pf-manual-fields">
                                  <input type="text" className="field-input"
                                    placeholder="Material composition, e.g. 65% organic cotton (GOTS certified), 35% recycled polyester"
                                    value={productFacts.materialComposition}
                                    onChange={(e) => setProductFacts((p) => ({ ...p, materialComposition: e.target.value }))}
                                  />
                                  <div className="cert-checkboxes" style={{ marginTop: 8 }}>
                                    {["GOTS", "Soil Association", "RWS", "OEKO-TEX", "PAS 2060", "Bluesign", "RCS", "FSC"].map((cert) => (
                                      <label key={cert} className="cert-checkbox-label">
                                        <input type="checkbox"
                                          checked={productFacts.certificationsHeld.includes(cert)}
                                          onChange={(e) => setProductFacts((p) => ({
                                            ...p,
                                            certificationsHeld: e.target.checked
                                              ? [...p.certificationsHeld, cert]
                                              : p.certificationsHeld.filter((c) => c !== cert),
                                          }))}
                                        />
                                        {cert}
                                      </label>
                                    ))}
                                  </div>
                                  <input type="text" className="field-input" style={{ marginTop: 8 }}
                                    placeholder="Other facts, e.g. Water audit 2023, 40% reduction vs 2021 baseline, ref WA-2023-041"
                                    value={productFacts.additionalFacts}
                                    onChange={(e) => setProductFacts((p) => ({ ...p, additionalFacts: e.target.value }))}
                                  />
                                </div>
                              )}
                            </div>
                          )}
                        </div>
                      )}

                      <div className="editor-footer">
                        <span className={`helper-text${inputText.length > MAX_DESCRIPTION_LENGTH ? " helper-text-warning" : ""}`}>
                          {inputText.length > MAX_DESCRIPTION_LENGTH
                            ? `${inputText.length} / ${MAX_DESCRIPTION_LENGTH} characters, over the limit, checks will be rejected`
                            : saveLine}
                        </span>
                        <div className="editor-actions field-row-actions">
                          <button
                            type="button"
                            className="btn-secondary"
                            onClick={() => myProduct.actions.saveNow()}
                            disabled={!myProduct.editable || myProduct.status === STATUS.IN_REVIEW}
                            title={myProduct.status === STATUS.IN_REVIEW ? "In review already -- use Submit to send your edit back into the queue." : undefined}
                          >
                            Save draft
                          </button>
                          <button type="button" className="btn-ghost" onClick={handleDiscard} disabled={!myProduct.editable}>
                            Discard
                          </button>
                        </div>
                      </div>
                    </>
                  )}

                  {activeTab === "examples" && (
                    <div className="example-list">
                      {exampleTexts.map((example, idx) => (
                        <button
                          key={idx}
                          type="button"
                          className="example-card"
                          onClick={() => {
                            setProductName(example.name);
                            handleDescriptionChange(example.copy);
                            setIsGuardApproved(false);
                            setLastApprovedText("");
                            setActiveTab("description");
                          }}
                        >
                          <p className="example-name">{example.name}</p>
                          <p className="example-copy">{example.copy}</p>
                          <span className="example-action">Use this example in description box</span>
                        </button>
                      ))}
                    </div>
                  )}

                  {activeTab === "guidance" && (
                    <div className="guidance-grid">
                      <div className="guidance-panel green">
                        <h4>CMA Green Claims Code</h4>
                        <ul>
                          <li>Claims must be truthful and accurate</li>
                          <li>Claims must be clear and unambiguous</li>
                          <li>Claims must not hide important information</li>
                          <li>Comparisons must be fair and meaningful</li>
                          <li>Claims should consider full product lifecycle</li>
                          <li>Claims must be substantiated with evidence</li>
                        </ul>
                      </div>
                      <div className="guidance-panel blue">
                        <h4>ASA/CAP Environmental Guidance</h4>
                        <ul>
                          <li>Basis for claims must be clear to consumers</li>
                          <li>Unqualified claims can mislead</li>
                          <li>Lifecycle context should be included</li>
                          <li>Claims must be backed by credible evidence</li>
                          <li>Avoid absolute claims without robust proof</li>
                        </ul>
                      </div>
                    </div>
                  )}
                </section>

                <section className="glass-panel card guard-card compliance-panel-column">
                  <h2 className="card-title">Claims & sustainability</h2>

                  {canShowComplianceScore && (
                    <p className="guard-summary-line">
                      {effectiveComplianceScore}% {String(effectiveRiskLevel || "unknown").toLowerCase()} risk
                      {summaryStatusText ? ` · ${summaryStatusText}` : ""}
                    </p>
                  )}

                  {keyIssueCards.length > 0 && (
                    <div className="key-issues-summary">
                      {keyIssueCards.map((card) => (
                        <div key={card.key} className="finding-card key-issue-card">
                          <p className="key-issue-title">{card.title}</p>
                          {/* With only one item, this line would just repeat the finding card below word for word. */}
                          {card.items.length > 1 && <p className="key-issue-risk">{card.riskyLine}</p>}
                          <span className="key-issue-count">
                            {card.items.length} issue{card.items.length > 1 ? "s" : ""}
                          </span>
                        </div>
                      ))}
                    </div>
                  )}

                  {showProceedWithCareBanner && (
                    <div className="approval-banner caution active">
                      Proceed with care. Internal legal review required.
                    </div>
                  )}

                  {!showReadyBanner && !showProceedWithCareBanner && textChangedSinceApproval && (
                    <div className="approval-banner changed">
                      Edited since last approval.
                    </div>
                  )}

                  {isAnalyzing && (
                    <div className="state-card">
                      <div className="spinner" />
                      <p>Checking claim risk...</p>
                    </div>
                  )}

                  {!isAnalyzing && !analysis && (
                    <div className="state-card empty">
                      <Shield size={42} />
                      <p>Start writing for feedback</p>
                    </div>
                  )}

                  {!isAnalyzing && analysis && (
                    <div className={`review-stack ${isReviewLocked ? "review-locked" : ""}`}>

                      {/* One-at-a-time issue flow */}

                      {currentIssue ? (
                        (() => {
                          const { finding, idx, issueKey } = currentIssue;
                          const suggestionText = getSuggestionTextForIssue(finding);
                          return (
                            <article
                              className="finding-card one-at-a-time"
                              style={{ borderColor: `${getRiskColor(finding.severity)}55` }}
                            >
                              <div className="finding-head">
                                <span className="severity-pill" style={{ background: getRiskColor(finding.severity) }}>
                                  {String(finding.severity || "medium")}
                                </span>
                                <span className="issue-counter">{resolvedCount + 1} of {findingsWithMeta.length}</span>
                              </div>

                              <p className="finding-category">{getIssueDisplayTitle(finding)}</p>
                              <p className="finding-text one-line">{getIssueSummaryText(finding)}</p>

                              {finding.category !== "Connection Error" && finding.category !== "Input rejected" && (() => {
                                const isReductionClaim = requiresEvidenceForIssue(finding);
                                const rewriteShown = Boolean(showRewriteByKey[issueKey]);
                                const evFields = normalizeEvidenceFields(reductionInputsByIssue[issueKey], finding);
                                const evComplete = isEvidenceComplete(evFields);

                                if (isReductionClaim && !rewriteShown) {
                                  // path A, evidence form first, no rewrite yet
                                  return (
                                    <div className="evidence-fields compact">
                                      <p className="evidence-fields-title">Back up this claim with evidence</p>
                                      <div className="evidence-fields-grid">
                                        <input
                                          className="field-input"
                                          placeholder="Baseline"
                                          value={evFields.baseline || ""}
                                          onChange={(e) => updateIssueEvidenceField(issueKey, "baseline", e.target.value)}
                                          disabled={isReviewLocked}
                                        />
                                        <input
                                          className="field-input"
                                          type="number"
                                          min="0"
                                          step="0.1"
                                          placeholder="% Reduction"
                                          value={evFields.reductionPercentage || ""}
                                          onChange={(e) => updateIssueEvidenceField(issueKey, "reductionPercentage", e.target.value)}
                                          disabled={isReviewLocked}
                                        />
                                        <input
                                          className="field-input"
                                          placeholder="Timeframe"
                                          value={evFields.timeframe || ""}
                                          onChange={(e) => updateIssueEvidenceField(issueKey, "timeframe", e.target.value)}
                                          disabled={isReviewLocked}
                                        />
                                        <input
                                          className="field-input"
                                          placeholder="Evidence reference"
                                          value={evFields.evidenceReference || ""}
                                          onChange={(e) => updateIssueEvidenceField(issueKey, "evidenceReference", e.target.value)}
                                          disabled={isReviewLocked}
                                        />
                                      </div>
                                      <div className="suggestion-actions">
                                        <button
                                          type="button"
                                          className="btn-secondary apply-btn"
                                          onClick={() => {
                                            isValidatingEvidenceRef.current = true;
                                            handleKeepDecision(issueKey, "Evidence provided");
                                          }}
                                          disabled={isReviewLocked || !evComplete}
                                          title={evComplete ? "Keep claim with your evidence on file" : "Fill in all four fields to keep this claim"}
                                        >
                                          Keep with evidence
                                        </button>
                                        <button
                                          type="button"
                                          className="btn-ghost apply-btn"
                                          onClick={() => setShowRewriteByKey((prev) => ({ ...prev, [issueKey]: true }))}
                                          disabled={isReviewLocked}
                                        >
                                          Keep wording
                                        </button>
                                      </div>
                                    </div>
                                  );
                                }

                                // path B (other claims, or reduction after "Keep wording"), rewrite first. "Apply suggestion" only makes sense once the AI has made a real rewrite (isAiAnalysis === true), otherwise suggestionText is just the rule engine's generic guidance, shown but not offered for insertion
                                return (
                                  <div className="suggestion-block">
                                    <p className="suggestion-label">{isAiAnalysis ? "Compliant rewrite" : "Rule guidance"}</p>
                                    {!isAiAnalysis && analysis?.aiStatus && analysis.aiStatus !== "Skipped" && (
                                      <p className="suggestion-ai-unavailable" role="status">
                                        {describeAiUnavailable(analysis.aiStatus)}
                                      </p>
                                    )}
                                    <p className="suggestion-text-body">{suggestionText}</p>
                                    <div className="suggestion-actions">
                                      <button
                                        type="button"
                                        className="btn-secondary apply-btn"
                                        onClick={() => handleApplyDecision(finding, idx)}
                                        disabled={isReviewLocked || !isAiAnalysis || !suggestionText}
                                        title={!isAiAnalysis ? "Only available once a real AI rewrite is ready. Try Check with AI again, or use Keep with a reason." : undefined}
                                      >
                                        Apply suggestion
                                      </button>
                                      {/* A critical (high-severity) issue may be kept too, not only applied — but never one that contradicts the verified facts, which must be corrected since it's simply false. */}
                                      {!isReductionClaim && finding.category !== "product_facts_mismatch" && (() => {
                                        const isCritical = finding.severity === "high";
                                        const minLength = isCritical ? MIN_CRITICAL_KEEP_REASON : 1;
                                        const reasonLength = (keepJustTextByKey[issueKey] || "").trim().length;

                                        return keepJustPendingByKey[issueKey] ? (
                                          <>
                                            <textarea
                                              className="field-input keep-just-input"
                                              placeholder={
                                                isCritical
                                                  ? `Briefly explain why this wording is kept (at least ${MIN_CRITICAL_KEEP_REASON} characters)`
                                                  : "Briefly explain why this wording is kept (required)"
                                              }
                                              rows={2}
                                              value={keepJustTextByKey[issueKey] || ""}
                                              onChange={(e) => setKeepJustTextByKey((prev) => ({ ...prev, [issueKey]: e.target.value }))}
                                              disabled={isReviewLocked}
                                            />
                                            {isCritical && (
                                              <p className="helper-text">Needs a Senior Editor's override before publishing.</p>
                                            )}
                                            <button
                                              type="button"
                                              className="btn-secondary apply-btn"
                                              disabled={isReviewLocked || reasonLength < minLength}
                                              onClick={() => {
                                                const just = (keepJustTextByKey[issueKey] || "").trim();
                                                if (just.length < minLength) return;
                                                handleKeepDecision(issueKey, just);
                                                setKeepJustPendingByKey((prev) => ({ ...prev, [issueKey]: false }));
                                              }}
                                            >
                                              Confirm keep
                                            </button>
                                            <button
                                              type="button"
                                              className="btn-ghost apply-btn"
                                              onClick={() => setKeepJustPendingByKey((prev) => ({ ...prev, [issueKey]: false }))}
                                              disabled={isReviewLocked}
                                            >
                                              Cancel
                                            </button>
                                          </>
                                        ) : (
                                          <button
                                            type="button"
                                            className="btn-ghost apply-btn"
                                            onClick={() => setKeepJustPendingByKey((prev) => ({ ...prev, [issueKey]: true }))}
                                            disabled={isReviewLocked}
                                          >
                                            Keep with a reason
                                          </button>
                                        );
                                      })()}
                                    </div>
                                  </div>
                                );
                              })()}

                              {(finding.caseReferences || []).length > 0 && (
                                <div className="case-references">
                                  <p className="case-references-label">Precedent cases</p>
                                  {finding.caseReferences.map((c) => (
                                    <div key={c.id} className="case-reference-item">
                                      <div className="case-reference-row">
                                        <span className={`case-badge case-badge--${c.body.toLowerCase()}`}>{c.body}</span>
                                        <span className="case-brand">{c.brand}</span>
                                        <span className="case-year">{c.year}</span>
                                        <span className="case-outcome">{c.outcome}</span>
                                      </div>
                                      <p className="case-summary">{c.summary}</p>
                                    </div>
                                  ))}
                                </div>
                              )}

                              {getRegulationAlertsForFinding(finding).map((u) => (
                                <a
                                  key={u.id}
                                  href={u.url}
                                  target="_blank"
                                  rel="noreferrer"
                                  className="reg-alert-inline"
                                >
                                  <span className="reg-alert-source">{u.source}</span>
                                  {u.title}
                                </a>
                              ))}
                            </article>
                          );
                        })()
                      ) : findingsWithMeta.length > 0 ? (
                        <div className="state-card resolved">
                          <CheckCircle size={36} />
                        </div>
                      ) : null}


                      {analysis.aiExplanation && !(analysis.groupedFindings || []).length && (
                        <div className="review-note">
                          <h4 className="subheading blue">
                            <Lightbulb size={16} />
                            Suggestions to tidy this claim
                          </h4>
                          <p>{analysis.aiExplanation}</p>
                        </div>
                      )}

                      <div className="mark-ready-section">
                        <p className="guard-disclaimer">
                          This tool flags potential risks, but your business remains responsible for final
                          environmental claims.
                        </p>
                        {markReadyMessage && (
                          <p className="mark-ready-message" role="status">{markReadyMessage}</p>
                        )}
                      </div>

                      {Array.isArray(analysis.references) && analysis.references.length > 0 && (
                        <div className="reference-card">
                          <h4 className="subheading blue">
                            <ExternalLink size={16} />
                            References
                          </h4>
                          <div className="reference-list">
                            {analysis.references.map((reference, index) => {
                              const refName = String(reference?.name || "").trim();
                              const refUrl = String(reference?.url || "").trim();
                              const key = `${refName || refUrl || "reference"}-${index}`;

                              if (!refUrl) {
                                return (
                                  <span key={key}>
                                    {refName || "Reference"}
                                  </span>
                                );
                              }

                              return (
                                <a key={key} href={refUrl} target="_blank" rel="noreferrer">
                                  {refName || refUrl}
                                  <ExternalLink size={13} />
                                </a>
                              );
                            })}
                          </div>
                        </div>
                      )}
                    </div>
                  )}
                </section>

                <section className="glass-panel card details-card">
                  <h2 className="card-title">Product details</h2>
                  <label className="field-label" htmlFor="product-category">Product Category (optional)</label>
                  <input id="product-category" className="field-input" value={primaryCategory} onChange={(e) => setPrimaryCategory(e.target.value)} />

                  <label className="field-label" htmlFor="product-subcategory">Product Subcategory (optional)</label>
                  <input id="product-subcategory" className="field-input" value={secondaryCategory} onChange={(e) => setSecondaryCategory(e.target.value)} />

                  <label className="field-label" htmlFor="product-tags">Product Tags (optional, comma separated)</label>
                  <input id="product-tags" className="field-input" value={productTags} onChange={(e) => setProductTags(e.target.value)} />
                </section>

                {canPublishProduct && <section className="glass-panel card sales-card sales-channel-card">
                  <h2 className="card-title">Actions</h2>
                  <div className="sales-actions">
                    {canPublishProduct && (
                      <button
                        type="button"
                        className="btn-primary"
                        onClick={handleAddProduct}
                        disabled={!canSignOff || !myProduct.editable}
                        title={canPublish ? "Ready to publish" : "Resolve all findings before publishing."}
                      >
                        Add Product
                      </button>
                    )}
                    <button
                      type="button"
                      className="btn-secondary"
                      onClick={handleSubmitForReview}
                      disabled={!canPublish || !myProduct.editable}
                      title={canPublish ? "Save to Pending Review without publishing yet" : "Resolve all findings before submitting."}
                    >
                      Submit
                    </button>
                  </div>
                </section>}
              </div>
            </div>
          ) : activeSidebarItem === "home" || activeSidebarItem === "overview" ? (
            isCopywriter ? (
              <MyHomeView overview={myOverview} submissions={mySubmissions} onRetry={reloadMyWork} onOpenProduct={openProduct} />
            ) : (
              <DashboardOverviewView dashboardStats={dashboardStats} recentClaims={recentClaims} isLoadingStats={isLoadingStats} error={statsError} />
            )
          ) : activeSidebarItem === "reports" ? (
            <ReportsView regUpdates={regUpdates} isLoadingUpdates={isLoadingUpdates} error={regUpdatesError} />
          ) : activeSidebarItem === "product" && isCopywriter && activeSidebarSubItem === "my-products" ? (
            <MyWorkView drafts={myDrafts} submissions={mySubmissions} onRetry={reloadMyWork} onOpenProduct={openProduct} onDeleteDraft={handleDeleteDraftFromList} />
          ) : activeSidebarItem === "product" && activeSidebarSubItem === "product-lists" ? (
            <AnalysisHistoryView recentClaims={recentClaims} isLoadingClaims={isLoadingClaims} />
          ) : activeSidebarItem === "data-transfer" && canPublishProduct ? (
            <DataTransferView
              runSeedImport={runSeedImport}
              isSeeding={isSeeding}
              seedResult={seedResult}
              exportAuditCsv={exportAuditCsv}
              isExportingAudit={isExportingAudit}
              auditExportError={auditExportError}
            />
          ) : activeSidebarItem === "pending-review" ? (
            reviewItem ? (
              <ReviewPanel
                key={reviewItem.productId}
                item={reviewItem}
                api={api}
                canAct={canPublishProduct}
                canOverride={canOverride}
                canEditFacts={canEditFacts}
                onBack={() => setReviewItem(null)}
                onDone={handleReviewDone}
                onEditFacts={(item) => {
                  factsToOpenRef.current = { productId: item.productId, productName: item.productName };
                  setActiveSidebarItem("verified-facts");
                }}
              />
            ) : (
              <PendingReviewView
                pendingReviews={pendingReviews}
                isLoadingPendingReviews={isLoadingPendingReviews}
                onReview={setReviewItem}
                message={queueMessage}
                error={pendingReviewsError}
              />
            )
          ) : activeSidebarItem === "verified-facts" && canPublishProduct ? (
            <VerifiedFactsView api={api} canEdit={canEditFacts} focus={factsFocus} onFocusChange={setFactsFocus} />
          ) : activeSidebarItem === "audit-trail" && canPublishProduct ? (
            <AuditTrailView api={api} />
          ) : activeSidebarItem === "help" ? (
            <HelpView />
          ) : activeSidebarItem === "account-settings" ? (
            <AccountSettingsView user={user} />

          ) : activeSidebarItem === "activity" ? (
            <ActivityView activity={myActivity} />
          ) : activeSidebarItem === "products-footer" || activeSidebarSubItem === "products-sub" ? (
            <ProductInventoryView recentClaims={recentClaims} error={claimsError} />
          ) : activeSidebarItem === "exit" ? (
            <ExitView onLogout={onLogout} onCancel={() => setActiveSidebarItem("home")} />
          ) : (
            <div className="placeholder-view glass-panel card">
              <div className="placeholder-content">
                <h3>{mainNavItems.find(i => i.id === activeSidebarItem)?.label || "Page"} Not Implemented</h3>
                <p>Not available yet. Use <strong>Product &gt; Add Product</strong> instead.</p>
                <button
                  type="button"
                  className="btn-primary"
                  onClick={() => {
                    setActiveSidebarItem("product");
                    setActiveSidebarSubItem("add-product");
                  }}
                >
                  Back to Add Product
                </button>
              </div>
            </div>
          )}
        </main>
      </div>

    </div>
  );
};

export default GreenClaimsGuard;
