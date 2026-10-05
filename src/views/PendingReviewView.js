import React from 'react';

// what's waiting for a senior editor, shows who submitted it, the market, AI check status, and if it needs an override
const PendingReviewView = ({ pendingReviews, isLoadingPendingReviews, onReview, message, error }) => (
  <div className="product-list-view">
    <div className="glass-panel card list-card">
      <div className="list-header">
        <h3 className="card-title">Pending Review</h3>
      </div>
      {message && <div className="import-feedback success" role="status">{message}</div>}
      {error && <div className="import-feedback error" role="alert">{error} Leave this page and come back to try again.</div>}
      {isLoadingPendingReviews ? (
        <div className="state-card">
          <div className="spinner" />
          <p>Loading pending reviews...</p>
        </div>
      ) : error ? null : (
        <table className="claims-table">
          <thead>
            <tr>
              <th>Product</th>
              <th>Description</th>
              <th>Submitted</th>
              <th>By</th>
              <th>Market</th>
              <th>AI Checks</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {pendingReviews.map((item) => (
              <tr key={item.productId}>
                <td>{item.productName}</td>
                <td className="claim-text-cell" title={item.finalDescription}>
                  {item.finalDescription.substring(0, 80)}...
                </td>
                <td>{new Date(item.submittedAt).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" })}</td>
                <td>
                  {item.submittedByEmail || item.submittedByUserId || "Unknown"}
                  {item.isOwnSubmission && <> <span className="status-pill neutral">Yours</span></>}
                </td>
                <td>{item.market || "UK"}</td>
                <td>
                  {item.needsOverride && <span className="status-pill danger">Needs override</span>}{" "}
                  <span className={`status-pill ${item.aiStatus === "Ok" ? "safe" : "warning"}`}>
                    {item.aiStatus === "Ok" ? "Checked" : "Not checked"}
                  </span>
                </td>
                <td>
                  <button type="button" className="btn-primary" onClick={() => onReview(item)}>
                    Review
                  </button>
                </td>
              </tr>
            ))}
            {pendingReviews.length === 0 && (
              <tr>
                <td colSpan="7" className="empty-table">No reviews awaiting sign-off.</td>
              </tr>
            )}
          </tbody>
        </table>
      )}
    </div>
  </div>
);

export default PendingReviewView;
