import React from 'react';
import { Shield, ExternalLink } from 'lucide-react';

const DataTransferView = ({ runSeedImport, isSeeding, seedResult, exportAuditCsv, isExportingAudit, auditExportError }) => (
  <div className="data-transfer-view">
    <div className="transfer-grid">
      <div className="glass-panel card action-card">
        <div className="action-icon-circle"><Shield size={24} /></div>
        <h3 className="card-title">Import Reference Data</h3>
        <p className="card-description">Sync the local database with the latest public green claims seed data (CSV).</p>
        <button
          type="button"
          className="btn-primary"
          onClick={runSeedImport}
          disabled={isSeeding}
        >
          {isSeeding ? "Importing..." : "Run Seed Import"}
        </button>
        {seedResult && (
          <div className={`import-feedback ${seedResult.success ? "success" : "error"}`}>
            {seedResult.message}
          </div>
        )}
      </div>
      <div className="glass-panel card action-card">
        <div className="action-icon-circle"><ExternalLink size={24} /></div>
        <h3 className="card-title">Export Audit Trail</h3>
        <p className="card-description">Download all review decisions as a CSV.</p>
        <button type="button" className="btn-secondary" onClick={exportAuditCsv} disabled={isExportingAudit}>
          {isExportingAudit ? "Preparing..." : "Download CSV"}
        </button>
        {auditExportError && <div className="import-feedback error">{auditExportError}</div>}
      </div>
    </div>
  </div>
);

export default DataTransferView;
