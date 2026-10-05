import React, { useState } from "react";

const MIN_NOTE = 5;
const MAX_NOTE = 500;

const formatDate = (iso) => (iso ? new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" }) : "");

// read only here since every claim is checked against these facts, a copywriter can only ask for one to be added
const VerifiedFactsPanel = ({ facts, factsRequest, canRequest, onRequest }) => {
  const [open, setOpen] = useState(false);
  const [asking, setAsking] = useState(false);
  const [note, setNote] = useState("");

  const data = facts.data;
  const trimmed = note.trim();
  const noteOk = trimmed.length >= MIN_NOTE && trimmed.length <= MAX_NOTE;

  const send = async (event) => {
    event.preventDefault();
    if (!noteOk) return;
    if (await onRequest(trimmed)) {
      setAsking(false);
      setNote("");
    }
  };

  return (
    <div className="pf-panel">
      <button type="button" className="pf-toggle" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
        <span className="pf-toggle-label">Verified facts (read only)</span>
        <span className="pf-toggle-hint">{open ? "Hide ▲" : "Show ▼"}</span>
      </button>

      {open && (
        <div className="pf-body">
          {facts.state === "loading" && <p className="helper-text">Loading the verified facts...</p>}
          {facts.state === "error" && <p role="alert">The verified facts could not be loaded. Your copy is still checked by the rules.</p>}

          {(facts.state === "idle" || facts.state === "none") && (
            <p className="helper-text">No verified facts on file yet. Material, certification and origin claims can't be checked.</p>
          )}

          {facts.state === "loaded" && data && (
            <>
              <p><strong>Composition:</strong> {(data.materials || []).length > 0 ? data.materials.map((m) => `${Number(m.percentage)}% ${m.material}`).join(", ") : "Not recorded"}</p>
              <p><strong>Certifications:</strong> {(data.certifications || []).length > 0 ? data.certifications.join(", ") : "None recorded"}</p>
              <p><strong>Made in:</strong> {data.origin || "Not recorded"}</p>
              {data.verifiedAt && <p className="helper-text">Verified by a Senior Editor on {formatDate(data.verifiedAt)}.</p>}
            </>
          )}

          {factsRequest.state === "sent" && <div className="import-feedback success" role="status">{factsRequest.message}</div>}
          {factsRequest.state === "error" && <div className="import-feedback error" role="alert">{factsRequest.message}</div>}

          {canRequest && !asking && (
            <div>
              <button type="button" className="btn-secondary" onClick={() => setAsking(true)}>Request facts</button>
            </div>
          )}

          {canRequest && asking && (
            <form onSubmit={send}>
              <label className="field-label" htmlFor="facts-note">What is missing or wrong? (at least {MIN_NOTE} characters)</label>
              <textarea
                id="facts-note"
                className="field-textarea"
                rows={3}
                maxLength={MAX_NOTE}
                value={note}
                onChange={(e) => setNote(e.target.value)}
              />
              <div className="editor-actions">
                <button type="submit" className="btn-primary" disabled={!noteOk || factsRequest.state === "sending"}>Send request</button>
                <button type="button" className="btn-ghost" onClick={() => setAsking(false)}>Cancel</button>
              </div>
            </form>
          )}
        </div>
      )}
    </div>
  );
};

export default VerifiedFactsPanel;
