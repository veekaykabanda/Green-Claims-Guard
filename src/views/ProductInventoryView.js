import React from 'react';

const ProductInventoryView = ({ recentClaims, error }) => (
  <div className="product-list-view">
    <div className="glass-panel card list-card">
      <div className="list-header">
        <h3 className="card-title">Product Inventory</h3>
      </div>
      {error && <div className="import-feedback error" role="alert">{error} Leave this page and come back to try again.</div>}
      {error ? null : <table className="claims-table">
        <thead>
          <tr>
            <th>Status</th>
            <th>Product</th>
            <th>Score</th>
            <th>Date</th>
          </tr>
        </thead>
        <tbody>
          {recentClaims.map((claim) => (
            <tr key={claim.id}>
              <td><span className={`status-pill ${claim.overallRisk?.toLowerCase()}`}>{claim.overallRisk}</span></td>
              <td>{claim.inputText.substring(0, 60)}...</td>
              <td>{claim.complianceScore}%</td>
              <td>{new Date(claim.createdAt).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" })}</td>
            </tr>
          ))}
          {recentClaims.length === 0 && (
            <tr>
              <td colSpan="4" className="empty-table">No product inventory found.</td>
            </tr>
          )}
        </tbody>
      </table>}
    </div>
  </div>
);

export default ProductInventoryView;
