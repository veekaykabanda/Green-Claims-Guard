import React from "react";

// shows this instead of the app if login isn't set up, nothing pretends to be signed in
const NotConfigured = () => (
  <div
    role="alert"
    style={{
      display: "flex",
      alignItems: "center",
      justifyContent: "center",
      height: "100vh",
      fontFamily: "Helvetica Neue, sans-serif",
      flexDirection: "column",
      gap: 12,
      textAlign: "center",
      padding: "0 24px",
    }}
  >
    <strong>Login is not set up</strong>
    <p style={{ fontSize: "0.9rem", margin: 0, color: "#4b5563", maxWidth: 480 }}>
      Set REACT_APP_AUTH0_DOMAIN and REACT_APP_AUTH0_CLIENT_ID, then start the app again.
    </p>
  </div>
);

export default NotConfigured;
