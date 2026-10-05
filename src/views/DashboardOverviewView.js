import React from 'react';

const DashboardOverviewView = ({ dashboardStats, recentClaims, isLoadingStats, error }) => (
  error ? (
    <div className="dashboard-view">
      <div className="import-feedback error" role="alert">{error} Leave this page and come back to try again.</div>
    </div>
  ) : isLoadingStats && !dashboardStats ? (
    <div className="dashboard-view">
      <div className="state-card">
        <div className="spinner" />
        <p>Loading dashboard stats...</p>
      </div>
    </div>
  ) : (
  <div className="dashboard-view">
    <div className="stats-grid">
      <div className="glass-panel card stat-card">
        <h4>Checked This Week</h4>
        <div className="stat-value">{dashboardStats?.productsCheckedThisWeek || 0}</div>
        <p className="stat-label">Distinct products checked or submitted</p>
      </div>
      <div className="glass-panel card stat-card">
        <h4>Open Critical Issues</h4>
        <div className="stat-value text-danger">{dashboardStats?.openCriticalIssues || 0}</div>
        <p className="stat-label">Unresolved in each product's latest state</p>
      </div>
      <div className="glass-panel card stat-card">
        <h4>Pending AI Review</h4>
        <div className="stat-value">{dashboardStats?.itemsPendingAiReview || 0}</div>
        <p className="stat-label">Waiting on an AI check that didn't complete</p>
      </div>
      <div className="glass-panel card stat-card">
        <h4>Awaiting Publish</h4>
        <div className="stat-value">{dashboardStats?.submissionsWaitingForPublish || 0}</div>
        <p className="stat-label">Submitted, ready for a Senior Editor</p>
      </div>
      <div className="glass-panel card stat-card">
        <h4>Overrides This Month</h4>
        <div className="stat-value text-success">{dashboardStats?.overridesThisMonth || 0}</div>
        <p className="stat-label">Published against a blocking reason</p>
      </div>
    </div>

    <div className="dashboard-content-grid">
      <div className="glass-panel card chart-card">
        <h3 className="card-title">Most Flagged Phrases</h3>
        {dashboardStats?.mostFlaggedPhrases?.length > 0 ? (
          <div className="category-list">
            {dashboardStats.mostFlaggedPhrases.map((item, idx) => (
              <div key={idx} className="category-row">
                <span className="category-name">{item.phrase}</span>
                <div className="category-bar-wrapper">
                  <div
                    className="category-bar"
                    style={{ width: `${(item.count / dashboardStats.mostFlaggedPhrases[0].count) * 100}%` }}
                  />
                </div>
                <span className="category-count">{item.count}</span>
              </div>
            ))}
          </div>
        ) : (
          <div className="empty-state">No issues recorded yet.</div>
        )}
      </div>

      <div className="glass-panel card trend-card">
        <h3 className="card-title">Recent Activity</h3>
        <div className="activity-feed">
          {(recentClaims.slice(0, 5)).map((claim) => (
            <div key={claim.id} className="activity-item">
              <div className={`activity-indicator ${claim.overallRisk?.toLowerCase()}`} />
              <div className="activity-info">
                <p className="activity-text">{claim.inputText.substring(0, 60)}...</p>
                <span className="activity-date">{new Date(claim.createdAt).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" })}</span>
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  </div>
  )
);

export default DashboardOverviewView;
