import React, { useEffect, useState } from "react";
import { useAsync } from "../hooks/useAsync";
import { describeError } from "../utils/errors";
import { MIN_FACTS_REASON } from "../utils/reviewRules";

const emptyRow = () => ({ material: "", percentage: "" });

// what every claim gets checked against, needs the edit:product-facts permission and a reason to change, and gets logged with before/after
export function validateFacts({ materials, reason }) {
  const errors = [];
  const rows = materials.filter((m) => m.material.trim() || String(m.percentage).trim());
  if (rows.some((m) => !m.material.trim())) errors.push("Every material needs a name.");
  if (rows.some((m) => !(Number(m.percentage) > 0 && Number(m.percentage) <= 100))) errors.push("Each percentage must be above 0 and at most 100.");
  const names = rows.map((m) => m.material.trim().toLowerCase());
  if (new Set(names).size !== names.length) errors.push("Each material may be listed once.");
  const total = rows.reduce((sum, m) => sum + (Number(m.percentage) || 0), 0);
  if (rows.length > 0 && Math.abs(total - 100) > 0.5) errors.push(`The percentages add up to ${Number(total.toFixed(2))}%. They must total 100%.`);
  if (reason.trim().length < MIN_FACTS_REASON) errors.push(`Say why the facts are changing, in at least ${MIN_FACTS_REASON} characters.`);
  return errors;
}

const FactsEditor = ({ api, productId, productName, canEdit, onBack, onSaved }) => {
  const facts = useAsync(() => api.getFacts(productId), [api, productId]);
  const [materials, setMaterials] = useState([emptyRow()]);
  const [certifications, setCertifications] = useState("");
  const [origin, setOrigin] = useState("");
  const [reason, setReason] = useState("");
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState(null); // { tone, text }

  useEffect(() => {
    if (facts.status !== "ok" || !facts.data) return;
    const rows = (facts.data.materials || []).map((m) => ({ material: m.material, percentage: String(Number(m.percentage)) }));
    setMaterials(rows.length ? rows : [emptyRow()]);
    setCertifications((facts.data.certifications || []).join(", "));
    setOrigin(facts.data.origin || "");
  }, [facts.status, facts.data]);

  const errors = validateFacts({ materials, reason });
  const setRow = (index, patch) => setMaterials((rows) => rows.map((row, i) => (i === index ? { ...row, ...patch } : row)));

  const save = async () => {
    setSaving(true);
    setMessage(null);
    try {
      await api.saveFacts(productId, {
        materials: materials.filter((m) => m.material.trim()).map((m) => ({ material: m.material.trim(), percentage: Number(m.percentage) })),
        certifications: certifications.split(",").map((c) => c.trim()).filter(Boolean),
        origin: origin.trim() || null,
        reason: reason.trim(),
      });
      setReason("");
      setMessage({ tone: "success", text: "Saved." });
      facts.reload();
      if (onSaved) onSaved();
    } catch (e) {
      setMessage({ tone: "error", text: describeError(e) });
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="product-list-view">
      <div className="glass-panel card list-card">
        <div className="list-header">
          <h3 className="card-title">Verified facts: {productName}</h3>
          <button type="button" className="btn-ghost" onClick={onBack}>Back</button>
        </div>

        {facts.status === "loading" && !facts.data && <p className="helper-text">Loading the facts...</p>}
        {facts.status === "error" && <div className="import-feedback error" role="alert">{describeError(facts.error)}</div>}

        {!canEdit && (
          <div className="import-feedback warning">You don't have permission to change the verified facts.</div>
        )}

        <fieldset disabled={!canEdit || saving} style={{ border: 0, padding: 0, margin: 0 }}>
          <label className="field-label">Composition (percentages must total 100)</label>
          {materials.map((row, index) => (
            <div key={index} className="editor-actions" style={{ justifyContent: "flex-start" }}>
              <input className="field-input" aria-label={`Material ${index + 1}`} placeholder="Material, e.g. organic cotton" value={row.material} onChange={(e) => setRow(index, { material: e.target.value })} />
              <input className="field-input" aria-label={`Percentage ${index + 1}`} placeholder="%" inputMode="decimal" style={{ maxWidth: 90 }} value={row.percentage} onChange={(e) => setRow(index, { percentage: e.target.value })} />
              <button type="button" className="btn-ghost" onClick={() => setMaterials((rows) => (rows.length > 1 ? rows.filter((_, i) => i !== index) : [emptyRow()]))}>Remove</button>
            </div>
          ))}
          <div><button type="button" className="btn-secondary" onClick={() => setMaterials((rows) => [...rows, emptyRow()])}>Add a material</button></div>

          <label className="field-label" htmlFor="facts-certs">Certifications (separated by commas)</label>
          <input id="facts-certs" className="field-input" value={certifications} onChange={(e) => setCertifications(e.target.value)} />

          <label className="field-label" htmlFor="facts-origin">Made in</label>
          <input id="facts-origin" className="field-input" value={origin} onChange={(e) => setOrigin(e.target.value)} />

          <label className="field-label" htmlFor="facts-reason">Why are the facts changing? (at least {MIN_FACTS_REASON} characters)</label>
          <textarea id="facts-reason" className="field-textarea" rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
        </fieldset>

        {canEdit && errors.length > 0 && reason.trim().length > 0 && (
          <ul className="helper-text">{errors.map((e) => <li key={e}>{e}</li>)}</ul>
        )}
        {message && <div className={`import-feedback ${message.tone}`} role={message.tone === "error" ? "alert" : "status"}>{message.text}</div>}

        {canEdit && (
          <div className="editor-actions">
            <button type="button" className="btn-primary" disabled={errors.length > 0 || saving} onClick={save}>
              {saving ? "Saving..." : "Save the facts"}
            </button>
          </div>
        )}
      </div>
    </div>
  );
};

export default FactsEditor;
