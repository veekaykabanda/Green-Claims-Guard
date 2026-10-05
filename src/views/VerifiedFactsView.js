import React, { useState } from "react";
import { useAsync } from "../hooks/useAsync";
import { describeError } from "../utils/errors";
import FactsEditor from "./FactsEditor";

const formatDate = (iso) => (iso ? new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" }) : "");

// any senior editor can see these requests, changing the facts needs the edit:product-facts permission
const VerifiedFactsView = ({ api, canEdit, focus, onFocusChange }) => {
  const requests = useAsync(() => api.getFactsRequests(), [api]);
  const [local, setLocal] = useState(null);
  const selected = focus || local;
  const open = (product) => (onFocusChange ? onFocusChange(product) : setLocal(product));
  const close = () => {
    if (onFocusChange) onFocusChange(null);
    setLocal(null);
    requests.reload();
  };

  if (selected) {
    return <FactsEditor api={api} productId={selected.productId} productName={selected.productName} canEdit={canEdit} onBack={close} onSaved={requests.reload} />;
  }

  return (
    <div className="product-list-view">
      <div className="glass-panel card list-card">
        <div className="list-header">
          <h3 className="card-title">Verified Facts</h3>
        </div>
        <p className="helper-text">Writer requests for facts verification. Their claims are checked against what you record here.</p>

        {requests.status === "loading" && !requests.data ? (
          <div className="state-card"><div className="spinner" /><p>Loading requests...</p></div>
        ) : requests.status === "error" ? (
          <div className="state-card" role="alert">
            <p>{describeError(requests.error)}</p>
            <button type="button" className="btn-secondary" onClick={requests.reload}>Try again</button>
          </div>
        ) : (
          <table className="claims-table">
            <thead>
              <tr><th>Product</th><th>Asked by</th><th>What they asked</th><th>Facts</th><th>Date</th><th></th></tr>
            </thead>
            <tbody>
              {(requests.data || []).map((r) => (
                <tr key={r.id}>
                  <td><strong>{r.productName}</strong></td>
                  <td>{r.requestedByEmail || r.requestedByUserId}</td>
                  <td className="claim-text-cell" title={r.note}>{r.note}</td>
                  <td><span className={`status-pill ${r.hasFacts ? "safe" : "warning"}`}>{r.hasFacts ? "On file" : "None yet"}</span></td>
                  <td>{formatDate(r.createdAt)}</td>
                  <td>
                    <button type="button" className="btn-secondary" onClick={() => open({ productId: r.productId, productName: r.productName })}>
                      {canEdit ? "Open" : "View"}
                    </button>
                  </td>
                </tr>
              ))}
              {(requests.data || []).length === 0 && (
                <tr><td colSpan="6" className="empty-table">No one has asked for facts.</td></tr>
              )}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
};

export default VerifiedFactsView;
