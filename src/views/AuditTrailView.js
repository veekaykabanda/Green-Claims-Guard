import React, { useState } from "react";
import { useAsync } from "../hooks/useAsync";
import { describeError } from "../utils/errors";
import { describeAction } from "../utils/reviewRules";

export const PAGE_SIZE = 25;

const stamp = (iso) => (iso ? new Date(iso).toLocaleString() : "");
// date box gives something like 2026-09-25, server filters on the start and end of that day
const startOf = (day) => (day ? `${day}T00:00:00` : undefined);
const endOf = (day) => (day ? `${day}T23:59:59` : undefined);

// shows the audit trail a page at a time for the chosen dates, the export has the same rows
const AuditTrailView = ({ api }) => {
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [page, setPage] = useState(0);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState("");

  const audit = useAsync(
    () => api.getAudit({ from: startOf(from), to: endOf(to), skip: page * PAGE_SIZE, take: PAGE_SIZE }),
    [api, from, to, page]
  );

  const total = audit.data?.total || 0;
  const lastPage = Math.max(Math.ceil(total / PAGE_SIZE) - 1, 0);

  const exportCsv = async () => {
    setExporting(true);
    setExportError("");
    try {
      const url = URL.createObjectURL(await api.exportAudit({ from: startOf(from), to: endOf(to) }));
      const link = document.createElement("a");
      link.href = url;
      link.download = `audit-trail-${new Date().toISOString().slice(0, 10)}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (e) {
      setExportError(describeError(e));
    } finally {
      setExporting(false);
    }
  };

  const change = (setter) => (e) => {
    setter(e.target.value);
    setPage(0);
  };

  return (
    <div className="product-list-view">
      <div className="glass-panel card list-card">
        <div className="list-header">
          <h3 className="card-title">Audit Trail</h3>
          <div className="list-actions">
            <button type="button" className="btn-secondary" onClick={exportCsv} disabled={exporting}>{exporting ? "Preparing..." : "Export CSV"}</button>
          </div>
        </div>
        <p className="helper-text">Every decision, newest first. Rows can't be edited or deleted.</p>

        <div className="editor-actions" style={{ justifyContent: "flex-start" }}>
          <label className="field-label" htmlFor="audit-from">From</label>
          <input id="audit-from" type="date" className="field-input" style={{ maxWidth: 180 }} value={from} onChange={change(setFrom)} />
          <label className="field-label" htmlFor="audit-to">To</label>
          <input id="audit-to" type="date" className="field-input" style={{ maxWidth: 180 }} value={to} onChange={change(setTo)} />
        </div>
        {exportError && <div className="import-feedback error" role="alert">{exportError}</div>}

        {audit.status === "error" ? (
          <div className="state-card" role="alert">
            <p>{describeError(audit.error)}</p>
            <button type="button" className="btn-secondary" onClick={audit.reload}>Try again</button>
          </div>
        ) : audit.status === "loading" && !audit.data ? (
          <div className="state-card"><div className="spinner" /><p>Loading the audit trail...</p></div>
        ) : (
          <>
            <table className="claims-table">
              <thead>
                <tr><th>When</th><th>Who</th><th>What</th><th>Product</th><th>Reason or detail</th></tr>
              </thead>
              <tbody>
                {audit.data.rows.map((row) => (
                  <tr key={row.id}>
                    <td>{stamp(row.timestamp)}</td>
                    <td>{row.userEmail || row.userId || "System"}</td>
                    <td>{describeAction(row.action, row.outcome)}</td>
                    <td>{row.productName || "—"}</td>
                    <td className="claim-text-cell" title={row.justification || row.detail || ""}>
                      {row.action === "FactsChange" ? row.justification : row.justification || row.detail || "—"}
                    </td>
                  </tr>
                ))}
                {audit.data.rows.length === 0 && <tr><td colSpan="5" className="empty-table">Nothing in the audit trail for these dates.</td></tr>}
              </tbody>
            </table>
            <div className="editor-actions">
              <span className="helper-text">{total === 0 ? "0 rows" : `Rows ${page * PAGE_SIZE + 1} to ${Math.min((page + 1) * PAGE_SIZE, total)} of ${total}`}</span>
              <button type="button" className="btn-ghost" disabled={page === 0} onClick={() => setPage((p) => p - 1)}>Previous</button>
              <button type="button" className="btn-ghost" disabled={page >= lastPage} onClick={() => setPage((p) => p + 1)}>Next</button>
            </div>
          </>
        )}
      </div>
    </div>
  );
};

export default AuditTrailView;
