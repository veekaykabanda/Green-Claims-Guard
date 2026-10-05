import React from 'react';
import { ExternalLink } from 'lucide-react';

const ReportsView = ({ regUpdates, isLoadingUpdates, error }) => (
  <div className="reports-view">
    <div className="reports-header">
      <h3 className="section-subtitle">Regulatory Updates & Guidance</h3>
      <p className="section-description">Stay informed with the latest green claims enforcement news from CMA and ASA.</p>
    </div>
    {error && <div className="import-feedback error" role="alert">{error} Leave this page and come back to try again.</div>}
    <div className="reports-grid">
      {error ? null : isLoadingUpdates ? (
        <div className="glass-panel card empty-state">
          <p>Fetching latest updates from regulatory bodies...</p>
        </div>
      ) : regUpdates.length > 0 ? (
        regUpdates.map((update) => (
          <article key={update.id} className="glass-panel card update-card">
            <div className="update-header">
              <span className={`source-badge ${update.source?.toLowerCase()}`}>{update.source}</span>
              <span className="update-date">{new Date(update.publishedDate).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" })}</span>
            </div>
            <h4 className="update-title">{update.title}</h4>
            <p className="update-summary">{update.summary}</p>
            <div className="update-tags">
              {update.affectedClaimTypes.map((tag, idx) => (
                <span key={idx} className="chip">{tag}</span>
              ))}
            </div>
            <a href={update.url} target="_blank" rel="noreferrer" className="btn-ghost update-link">
              Read full update <ExternalLink size={14} />
            </a>
          </article>
        ))
      ) : (
        <div className="glass-panel card empty-state">
          <p>No regulatory updates available right now.</p>
        </div>
      )}
    </div>
  </div>
);

export default ReportsView;
