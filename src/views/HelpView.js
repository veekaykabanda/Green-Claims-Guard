import React from 'react';

const HelpView = () => (
  <div className="help-view">
    <div className="help-hero">
      <h1>How can we help?</h1>
      <p>Learn about the Green Claims Code and how to use the Guard to validate your product descriptions.</p>
    </div>
    <div className="help-grid">
      <div className="glass-panel card faq-card">
        <h4>What is the Green Claims Code?</h4>
        <p>A set of 6 principles from the CMA to ensure environmental claims are truthful, clear, and substantiated. The Guard uses these rules to flag issues in your copy.</p>
      </div>
      <div className="glass-panel card faq-card">
        <h4>How do I resolve a flag?</h4>
        <p>Click "Apply Suggestion" to use our AI-generated compliant copy, or "Keep with Justification" if you have evidence to support your original claim.</p>
      </div>
      <div className="glass-panel card faq-card">
        <h4>What documents do I need?</h4>
        <p>For claims like "Recycled Content" or "Carbon Neutral", the system will ask for specific certifications which you can upload for AI-verification.</p>
      </div>
    </div>
    <div className="help-footer-banner glass-panel">
      <p>Need more assistance? Ask a Senior Editor at your organisation.</p>
    </div>
  </div>
);

export default HelpView;
