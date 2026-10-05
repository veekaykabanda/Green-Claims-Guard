import React from "react";
import { describeError } from "../utils/errors";
import { STATUS_LABELS, isEditableStatus, mergeMyWork } from "../utils/myWork";

const PILL = { Draft: "neutral", InReview: "warning", SentBack: "danger", Published: "safe" };
const formatDate = (iso) => (iso ? new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" }) : "");

// your own products, draft and sent back open for editing, in review and published are just read only
const MyWorkView = ({ drafts, submissions, onRetry, onOpenProduct, onDeleteDraft }) => {
  const loading = (drafts.status === "loading" && !drafts.data) || (submissions.status === "loading" && !submissions.data);
  const failed = drafts.status === "error" ? drafts.error : submissions.status === "error" ? submissions.error : null;

  if (failed) {
    return (
      <div className="product-list-view">
        <div className="state-card" role="alert">
          <p>{describeError(failed)}</p>
          <button type="button" className="btn-secondary" onClick={onRetry}>Try again</button>
        </div>
      </div>
    );
  }

  if (loading) {
    return (
      <div className="product-list-view">
        <div className="state-card">
          <div className="spinner" />
          <p>Loading your products...</p>
        </div>
      </div>
    );
  }

  const rows = mergeMyWork(drafts.data, submissions.data);

  return (
    <div className="product-list-view">
      <div className="glass-panel card list-card">
        <div className="list-header">
          <h3 className="card-title">My Products</h3>
        </div>
        <table className="claims-table">
          <thead>
            <tr>
              <th>Product</th>
              <th>Status</th>
              <th>Market</th>
              <th>Updated</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.productId}>
                <td className="claim-text-cell" title={row.preview || row.productName}>
                  <strong>{row.productName}</strong>
                  {row.status === "SentBack" && row.sendBack?.reasonLabel && (
                    <div className="helper-text">Sent back: {row.sendBack.reasonLabel}</div>
                  )}
                </td>
                <td>
                  <span className={`status-pill ${PILL[row.status] || "neutral"}`}>{STATUS_LABELS[row.status] || row.status}</span>
                </td>
                <td>{row.market || "UK"}</td>
                <td>{formatDate(row.updatedAt)}</td>
                <td>
                  <button type="button" className="btn-secondary" onClick={() => onOpenProduct(row.productId)}>
                    {isEditableStatus(row.status) ? "Edit" : "View"}
                  </button>{" "}
                  {isEditableStatus(row.status) && (
                    <button
                      type="button"
                      className="btn-ghost"
                      aria-label={`Discard this draft: ${row.productName}`}
                      title="Discard this draft"
                      onClick={() => onDeleteDraft(row.productId)}
                    >
                      Discard
                    </button>
                  )}
                </td>
              </tr>
            ))}
            {rows.length === 0 && (
              <tr>
                <td colSpan="5" className="empty-table">You have no products yet. Choose Add Product to start one.</td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
};

export default MyWorkView;
