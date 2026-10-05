import React from "react";
import { describeError } from "../utils/errors";
import { describeAction } from "../utils/reviewRules";

const stamp = (iso) => (iso ? new Date(iso).toLocaleString() : "");

// shows your own actions from the audit trail, newest first
const ActivityView = ({ activity }) => (
  <div className="activity-view">
    <div className="glass-panel card list-card">
      <div className="list-header">
        <h3 className="card-title">Your Activity</h3>
      </div>

      {activity.status === "error" ? (
        <div className="state-card" role="alert">
          <p>{describeError(activity.error)}</p>
          <button type="button" className="btn-secondary" onClick={activity.reload}>Try again</button>
        </div>
      ) : activity.status === "loading" && !activity.data ? (
        <div className="state-card"><div className="spinner" /><p>Loading your activity...</p></div>
      ) : (activity.data || []).length === 0 ? (
        <div className="empty-state">You have not done anything yet.</div>
      ) : (
        <div className="activity-timeline">
          {activity.data.map((row, index) => (
            <div key={`${row.timestamp}-${index}`} className="timeline-item">
              <div className="timeline-dot" />
              <div className="timeline-content">
                <strong>{describeAction(row.action, row.outcome)}</strong>
                <span>{row.productName || ""}</span>
                {row.justification && <span>&ldquo;{row.justification}&rdquo;</span>}
                <small>{stamp(row.timestamp)}</small>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  </div>
);

export default ActivityView;
