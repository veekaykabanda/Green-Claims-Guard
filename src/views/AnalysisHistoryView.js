import React, { useMemo, useState } from 'react';
import { Search } from 'lucide-react';

const AnalysisHistoryView = ({ recentClaims, isLoadingClaims }) => {
  const [searchTerm, setSearchTerm] = useState("");

  const filteredClaims = useMemo(() => {
    const term = searchTerm.trim().toLowerCase();
    if (!term) return recentClaims;
    return recentClaims.filter((claim) =>
      claim.inputText?.toLowerCase().includes(term) || claim.overallRisk?.toLowerCase().includes(term)
    );
  }, [recentClaims, searchTerm]);

  return (
    <div className="product-list-view">
      <div className="glass-panel card list-card">
        <div className="list-header">
          <h3 className="card-title">Analysis History</h3>
          <div className="list-actions">
            <div className="topbar-search">
              <Search size={14} />
              <input
                type="text"
                placeholder="Filter history..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
              />
            </div>
          </div>
        </div>
        {isLoadingClaims ? (
          <div className="state-card">
            <div className="spinner" />
            <p>Loading analysis history...</p>
          </div>
        ) : (
          <table className="claims-table">
            <thead>
              <tr>
                <th>Status</th>
                <th>Product / Description</th>
                <th>Score</th>
                <th>Issues</th>
                <th>Date</th>
              </tr>
            </thead>
            <tbody>
              {filteredClaims.map((claim) => (
                <tr key={claim.id}>
                  <td>
                    <span className={`status-pill ${claim.overallRisk?.toLowerCase()}`}>
                      {claim.overallRisk}
                    </span>
                  </td>
                  <td className="claim-text-cell" title={claim.inputText}>
                    {claim.inputText.substring(0, 80)}...
                  </td>
                  <td>
                    <div className="score-badge">{claim.complianceScore}%</div>
                  </td>
                  <td>{claim.totalIssues}</td>
                  <td>{new Date(claim.createdAt).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" })}</td>
                </tr>
              ))}
              {filteredClaims.length === 0 && (
                <tr>
                  <td colSpan="5" className="empty-table">
                    {recentClaims.length === 0 ? "No recent analyses found." : "No analyses match your search."}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
};

export default AnalysisHistoryView;
