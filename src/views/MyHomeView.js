import React from "react";
import { describeError } from "../utils/errors";

const formatDate = (iso) => (iso ? new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" }) : "");

// your own home page, just your numbers and your own sent back work, not the whole team's
const MyHomeView = ({ overview, submissions, onRetry, onOpenProduct }) => {
  if (overview.status === "loading" && !overview.data) {
    return (
      <div className="dashboard-view">
        <div className="state-card">
          <div className="spinner" />
          <p>Loading your work...</p>
        </div>
      </div>
    );
  }

  if (overview.status === "error") {
    return (
      <div className="dashboard-view">
        <div className="state-card" role="alert">
          <p>{describeError(overview.error)}</p>
          <button type="button" className="btn-secondary" onClick={onRetry}>Try again</button>
        </div>
      </div>
    );
  }

  const o = overview.data;
  const sentBack = (submissions.data || []).filter((s) => s.status === "SentBack");
  const phrases = (o.topPhrases || []).slice(0, 5);

  return (
    <div className="dashboard-view">
      <div className="stats-grid">
        <div className="glass-panel card stat-card">
          <h4>Sent back</h4>
          <div className={`stat-value ${o.sentBack > 0 ? "text-danger" : ""}`}>{o.sentBack}</div>
          <p className="stat-label">{o.sentBack > 0 ? "Needs your attention" : "Nothing waiting for changes"}</p>
        </div>
        <div className="glass-panel card stat-card">
          <h4>Drafts</h4>
          <div className="stat-value">{o.drafts}</div>
          <p className="stat-label">Saved, not yet submitted</p>
        </div>
        <div className="glass-panel card stat-card">
          <h4>Awaiting sign-off</h4>
          <div className="stat-value">{o.awaitingSignOff}</div>
          <p className="stat-label">With a Senior Editor</p>
        </div>
        <div className="glass-panel card stat-card">
          <h4>Published</h4>
          <div className="stat-value text-success">{o.published}</div>
          <p className="stat-label">Signed off and live</p>
        </div>
      </div>

      <div className="dashboard-content-grid">
        <div className="glass-panel card list-card">
          <h3 className="card-title">Sent back to you</h3>
          {sentBack.length === 0 ? (
            <div className="empty-state">Nothing has been sent back.</div>
          ) : (
            <table className="claims-table">
              <thead>
                <tr>
                  <th>Product</th>
                  <th>Reason</th>
                  <th>Sent back</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {sentBack.map((item) => (
                  <tr key={item.productId}>
                    <td>{item.productName}</td>
                    <td>
                      <strong>{item.sendBack?.reasonLabel}</strong>
                      {item.sendBack?.comment && <div className="helper-text">{item.sendBack.comment}</div>}
                    </td>
                    <td>{formatDate(item.sendBack?.at || item.updatedAt)}</td>
                    <td>
                      <button type="button" className="btn-primary" onClick={() => onOpenProduct(item.productId)}>
                        Open
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div className="glass-panel card chart-card">
          <h3 className="card-title">Phrases you use most</h3>
          {phrases.length > 0 ? (
            <div className="category-list">
              {phrases.map((item) => (
                <div key={item.phrase} className="category-row">
                  <span className="category-name">&ldquo;{item.phrase}&rdquo;</span>
                  <span className="category-count">{item.count}</span>
                </div>
              ))}
            </div>
          ) : (
            <div className="empty-state">No flagged phrases yet.</div>
          )}
        </div>
      </div>
    </div>
  );
};

export default MyHomeView;
