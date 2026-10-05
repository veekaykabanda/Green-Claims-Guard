import React, { useEffect, useRef } from "react";
import { STATUS } from "../hooks/useMyProduct";

// focuses the notice as soon as it shows up, since it's usually the result of something done further down the page like submit
const ProductStatusBanners = ({ status, sendBack, notice, busy, onWithdraw, onStartNew }) => {
  const noticeRef = useRef(null);

  useEffect(() => {
    if (notice) noticeRef.current?.focus();
  }, [notice]);

  return (
    <div className="status-banners">
      {notice && (
        <div
          ref={noticeRef}
          tabIndex={-1}
          className={`import-feedback ${notice.tone === "ok" ? "success" : notice.tone === "warn" ? "warning" : "error"}`}
          role={notice.tone === "error" ? "alert" : "status"}
        >
          {notice.text}
        </div>
      )}

      {status === STATUS.SENT_BACK && sendBack && (
        <div className="import-feedback warning" role="status">
          Sent back by a Senior Editor: {sendBack.reasonLabel}
          {sendBack.comment && <div className="helper-text">{sendBack.comment}</div>}
        </div>
      )}

      {status === STATUS.IN_REVIEW && (
        <div className="import-feedback warning" role="status">
          In review.
          <div>
            <button type="button" className="btn-secondary" disabled={busy} onClick={onWithdraw}>
              {busy ? "Withdrawing..." : "Withdraw"}
            </button>{" "}
            <button type="button" className="btn-ghost" onClick={onStartNew}>Start a new product</button>
          </div>
        </div>
      )}

      {status === STATUS.PUBLISHED && (
        <div className="import-feedback success" role="status">
          This product is published, so its copy is locked.
          <div>
            <button type="button" className="btn-ghost" onClick={onStartNew}>Start a new product</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default ProductStatusBanners;
