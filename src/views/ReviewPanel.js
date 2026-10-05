import React, { useState } from "react";
import { useAsync } from "../hooks/useAsync";
import { describeError } from "../utils/errors";
import { MIN_COMMENT, MIN_OVERRIDE_REASON, SEND_BACK_REASONS, predictBlocks } from "../utils/reviewRules";

const formatDate = (iso) => (iso ? new Date(iso).toLocaleString() : "");

// everyone can open this panel, canAct just hides the publish/override/send back buttons a copywriter can't use anyway
const ReviewPanel = ({ item, api, canAct, canOverride, canEditFacts, onBack, onDone, onEditFacts }) => {
  const history = useAsync(() => api.getHistory(item.productId), [api, item.productId]);
  const facts = useAsync(() => api.getFacts(item.productId), [api, item.productId]);
  const requests = useAsync(() => api.getFactsRequests(), [api]);

  const [busy, setBusy] = useState(null); // "publish" | "override" | "sendback"
  const [blocked, setBlocked] = useState(null); // reasons the server gave when it refused to publish
  const [error, setError] = useState("");
  const [overrideReason, setOverrideReason] = useState("");
  const [category, setCategory] = useState("");
  const [comment, setComment] = useState("");
  // set when the writer changed the copy after this panel opened, since they can still edit while it's in review
  const [staleCopy, setStaleCopy] = useState(null);

  const predicted = predictBlocks(item);
  const blockReasons = blocked || predicted;
  const isBlocked = blockReasons.length > 0;
  const versions = history.data?.versions || [];
  const decisions = versions.length ? versions[versions.length - 1].decisions || [] : [];
  const myRequests = (requests.data || []).filter((r) => r.productId === item.productId);
  const overrideOk = overrideReason.trim().length >= MIN_OVERRIDE_REASON;
  const sendBackOk = Boolean(category) && comment.trim().length >= MIN_COMMENT;

  // checks the queue again before acting, so you don't publish or send back copy that's already changed
  const refuseIfStale = async () => {
    const latest = await api.getPendingReviews();
    const match = (latest || []).find((p) => p.productId === item.productId);
    if (match && match.finalDescription !== item.finalDescription) {
      setStaleCopy(match.finalDescription);
      return true;
    }
    return false;
  };

  const publish = async (reason) => {
    if (await refuseIfStale()) return;
    const overriding = Boolean(reason);
    const question = overriding
      ? "Publish this past the block? Your reason is recorded in the audit trail under your name."
      : "Publish this product? The copy is checked again now, and it goes live if it passes.";
    if (!window.confirm(question)) return;

    setBusy(overriding ? "override" : "publish");
    setError("");
    try {
      const result = await api.publish(item.productId, reason);
      onDone(result.overridden ? `${item.productName} was published with your override.` : `${item.productName} was published.`);
    } catch (e) {
      if (e.status === 409 || e.status === 403) {
        // The server's own reasons replace anything guessed beforehand.
        const reasons = e.body?.blockingReasons?.length ? e.body.blockingReasons : [e.message];
        setBlocked(reasons);
        setError(e.status === 403 ? e.message : "");
      } else {
        setError(describeError(e));
      }
    } finally {
      setBusy(null);
    }
  };

  const sendBack = async () => {
    if (await refuseIfStale()) return;
    if (!window.confirm("Send this back to its writer?")) return;
    setBusy("sendback");
    setError("");
    try {
      await api.sendBack(item.productId, category, comment.trim());
      onDone(`${item.productName} was sent back to its writer.`);
    } catch (e) {
      setError(describeError(e));
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="product-list-view">
      <div className="glass-panel card list-card">
        <div className="list-header">
          <h3 className="card-title">{item.productName}</h3>
          <button type="button" className="btn-ghost" onClick={onBack}>Back</button>
        </div>

        <p className="helper-text">
          Submitted {formatDate(item.submittedAt)} by {item.submittedByEmail || item.submittedByUserId || "an unknown user"}.{" "}
          {item.needsOverride && <span className="status-pill danger">Needs override</span>}{" "}
          {item.isOwnSubmission && <span className="status-pill neutral">Yours</span>}
        </p>

        <h4>Submitted copy</h4>
        <p className="claim-text-cell" style={{ whiteSpace: "pre-wrap" }}>{item.finalDescription}</p>

        <h4>What the writer decided</h4>
        {history.status === "loading" && !history.data ? (
          <p className="helper-text">Loading...</p>
        ) : decisions.length === 0 ? (
          <p className="helper-text">The writer did not apply or keep anything: nothing was flagged.</p>
        ) : (
          <table className="claims-table">
            <thead>
              <tr><th>Phrase</th><th>Decision</th><th>Reason given</th></tr>
            </thead>
            <tbody>
              {decisions.map((d) => (
                <tr key={d.issueId}>
                  <td>&ldquo;{d.phrase || d.category}&rdquo; {d.critical && <span className="status-pill danger">Critical</span>}</td>
                  <td>{d.decision === "Kept" ? "Kept the wording" : "Applied the rewrite"}</td>
                  <td>{d.reason || "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <h4>Verified facts</h4>
        {facts.status === "loading" && !facts.data ? (
          <p className="helper-text">Loading...</p>
        ) : facts.data?.status === "Loaded" ? (
          <>
            <p><strong>Composition:</strong> {(facts.data.materials || []).map((m) => `${Number(m.percentage)}% ${m.material}`).join(", ") || "Not recorded"}</p>
            <p><strong>Certifications:</strong> {(facts.data.certifications || []).join(", ") || "None recorded"}</p>
            <p><strong>Made in:</strong> {facts.data.origin || "Not recorded"}</p>
          </>
        ) : (
          <p className="helper-text">No verified facts on file yet. Composition, certification and origin claims can't be checked.</p>
        )}
        {myRequests.map((r) => (
          <p key={r.id} className="helper-text">
            The writer asked ({formatDate(r.createdAt)}): &ldquo;{r.note}&rdquo;
          </p>
        ))}
        {canEditFacts ? (
          <button type="button" className="btn-secondary" onClick={() => onEditFacts(item)}>Edit facts</button>
        ) : (
          <p className="helper-text">You don't have permission to change the verified facts.</p>
        )}

        <h4>Decide</h4>
        {staleCopy && (
          <div className="import-feedback warning" role="alert">
            The writer has changed this since you opened it. Go back and re-open it to review the current text:
            <p className="claim-text-cell" style={{ whiteSpace: "pre-wrap", marginTop: "8px" }}>{staleCopy}</p>
          </div>
        )}
        {!canAct ? (
          <p className="helper-text">Only a Senior Editor can publish, override or send this back.</p>
        ) : (
          <>
            {isBlocked && (
              <div className="import-feedback warning" role="alert">
                Publishing is blocked:
                <ul>{blockReasons.map((reason) => <li key={reason}>{reason}</li>)}</ul>
              </div>
            )}
            {error && <div className="import-feedback error" role="alert">{error}</div>}

            {item.canSignOff ? (
              <div className="editor-actions">
                <button type="button" className="btn-primary" disabled={isBlocked || busy !== null} onClick={() => publish()}>
                  {busy === "publish" ? "Publishing..." : "Publish"}
                </button>
              </div>
            ) : (
              <div className="import-feedback warning">You submitted this yourself, so another Senior Editor has to sign it off. You can still send it back.</div>
            )}

            {item.canSignOff && isBlocked && (canOverride ? (
              <div className="field-group">
                <label className="field-label" htmlFor="override-reason">Why should this be published anyway?</label>
                <textarea id="override-reason" className="field-textarea" rows={3} value={overrideReason} onChange={(e) => setOverrideReason(e.target.value)} />
                <div className="editor-actions">
                  <button type="button" className="btn-secondary" disabled={!overrideOk || busy !== null} onClick={() => publish(overrideReason.trim())}>
                    {busy === "override" ? "Publishing..." : "Override and publish"}
                  </button>
                </div>
              </div>
            ) : (
              <p className="helper-text">You can't override this. Send it back instead.</p>
            ))}

            <div className="field-group">
              <label className="field-label" htmlFor="send-back-reason">Which principle does it fall short of?</label>
              <select id="send-back-reason" className="field-input" value={category} onChange={(e) => setCategory(e.target.value)}>
                <option value="">Choose a reason</option>
                {SEND_BACK_REASONS.map((r) => <option key={r.code} value={r.code}>{r.label}</option>)}
              </select>
              <label className="field-label" htmlFor="send-back-comment">What needs to change?</label>
              <textarea id="send-back-comment" className="field-textarea" rows={3} value={comment} onChange={(e) => setComment(e.target.value)} />
              <div className="editor-actions">
                <button type="button" className="btn-secondary" disabled={!sendBackOk || busy !== null} onClick={sendBack}>
                  {busy === "sendback" ? "Sending back..." : "Send back"}
                </button>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
};

export default ReviewPanel;
