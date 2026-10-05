import React from 'react';
import { getPersona } from '../utils/personas';

const AccountSettingsView = ({ user }) => (
  <div className="settings-view">
    <div className="glass-panel card settings-card">
      <h3 className="card-title">Account Settings</h3>
      <div className="settings-grid">
        <div className="settings-row">
          <label className="field-label">User Email</label>
          <div className="field-input-wrapper">
            <input type="text" className="field-input" value={user?.email || "—"} disabled />
          </div>
        </div>
        <div className="settings-row">
          <label className="field-label">Role</label>
          <div className="field-input-wrapper">
            <input type="text" className="field-input" value={getPersona(user).label} disabled />
          </div>
        </div>
      </div>
    </div>
  </div>
);

export default AccountSettingsView;
