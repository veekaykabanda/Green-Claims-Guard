import React from 'react';

const ExitView = ({ onLogout, onCancel }) => (
  <div className="exit-view glass-panel card">
    <div className="placeholder-content">
      <h3>Ready to leave?</h3>
      <div style={{ display: "flex", gap: "12px", marginTop: "24px" }}>
        <button type="button" className="btn-primary" onClick={onLogout}>Sign Out Now</button>
        <button type="button" className="btn-secondary" onClick={onCancel}>Cancel</button>
      </div>
    </div>
  </div>
);

export default ExitView;
