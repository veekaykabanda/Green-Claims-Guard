import React, { useCallback } from 'react';
import ReactDOM from 'react-dom/client';
import './index.css';
import App from './App';
import reportWebVitals from './reportWebVitals';
import { Auth0Provider, useAuth0 } from '@auth0/auth0-react';
import { auth0Domain, auth0ClientId, auth0Audience, isAuth0Configured, PERMISSIONS_CLAIM } from './auth0Config';
import { getPersona, PERSONAS } from './utils/personas';
import NotConfigured from './auth/NotConfigured';

const centred = {
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  height: "100vh",
  fontFamily: "Helvetica Neue, sans-serif",
};

// signed in, but this account has no role yet (Copywriter or Senior Editor)
function NoAccess({ email, onLogout }) {
  return (
    <div role="alert" style={{ ...centred, flexDirection: "column", gap: "12px", textAlign: "center", padding: "0 24px" }}>
      <strong>No access</strong>
      <p style={{ fontSize: "0.9rem", margin: 0, color: "#4b5563", maxWidth: 420 }}>
        {email} is signed in, but has not been given a role yet. Ask a Senior Editor to assign you the Copywriter
        role, then sign in again.
      </p>
      <button onClick={onLogout} style={{ marginTop: "8px", padding: "8px 20px", background: "#111", color: "#fff", border: "none", borderRadius: "6px", cursor: "pointer", fontSize: "0.85rem" }}>
        Sign out
      </button>
    </div>
  );
}

// Auth0 inner component (must be inside Auth0Provider)
function Auth0Root() {
  const { isLoading, isAuthenticated, error, user, loginWithRedirect, logout, getAccessTokenSilently } = useAuth0();

  const getToken = useCallback(async () => {
    // getAccessTokenSilently can hang forever instead of erroring, so race it against a timeout to force a visible failure
    const timeout = new Promise((resolve) => setTimeout(() => resolve(null), 8000));
    try {
      return await Promise.race([
        getAccessTokenSilently({ authorizationParams: { audience: auth0Audience } }),
        timeout,
      ]);
    } catch {
      return null;
    }
  }, [getAccessTokenSilently]);

  const handleLogout = useCallback(() => {
    logout({ logoutParams: { returnTo: window.location.origin } });
  }, [logout]);

  // stays null until the token loads (hooks have to run before any return), so nobody sees "no access" too early
  const [permissions, setPermissions] = React.useState(null);
  React.useEffect(() => {
    if (!isAuthenticated) return;
    getToken().then(token => {
      if (!token) {
        setPermissions([]);
        return;
      }
      try {
        const payload = JSON.parse(atob(token.split('.')[1]));
        setPermissions(payload[PERMISSIONS_CLAIM] || []);
      } catch {
        // bad token means no permissions, no persona, no access
        setPermissions([]);
      }
    });
  }, [isAuthenticated, getToken]);

  if (isLoading) {
    return (
      <div style={{ ...centred, color: "#6b7280", fontSize: "0.9rem" }}>
        Loading…
      </div>
    );
  }

  if (error) {
    return (
      <div style={{ ...centred, flexDirection: "column", gap: "12px", color: "#b91c1c" }}>
        <strong>Authentication error</strong>
        <p style={{ fontSize: "0.85rem", margin: 0, color: "#6b7280" }}>{error.message}</p>
        <button onClick={() => loginWithRedirect()} style={{ marginTop: "8px", padding: "8px 20px", background: "#111", color: "#fff", border: "none", borderRadius: "6px", cursor: "pointer", fontSize: "0.85rem" }}>Try again</button>
      </div>
    );
  }

  if (!isAuthenticated) {
    loginWithRedirect({ authorizationParams: { audience: auth0Audience, scope: "openid profile email" } });
    return null;
  }

  if (permissions === null) {
    return (
      <div style={{ ...centred, color: "#6b7280", fontSize: "0.9rem" }}>
        Loading…
      </div>
    );
  }

  // figures out who someone is from the permissions on their token, same thing the server checks
  const appUser = { email: user.email, name: user.name, permissions };

  if (getPersona(appUser) === PERSONAS.NONE) {
    return <NoAccess email={user.email} onLogout={handleLogout} />;
  }

  // both roles share this same page, permissions decide what each one can see and do
  return (
    <App
      user={appUser}
      onLogout={handleLogout}
      getToken={getToken}
      // if sign-in goes bad, send them back to Auth0 then back to this same page
      onSessionExpired={() =>
        loginWithRedirect({
          appState: { returnTo: window.location.pathname + window.location.search + window.location.hash },
          authorizationParams: { audience: auth0Audience, scope: "openid profile email" },
        })
      }
    />
  );
}

// Root
function Root() {
  // no login set up, just say so, nothing pretends to be signed in
  if (!isAuth0Configured) {
    return <NotConfigured />;
  }

  return (
    <Auth0Provider
      domain={auth0Domain}
      clientId={auth0ClientId}
      // After signing in, go back to the page that was open, not the front door.
      onRedirectCallback={(appState) => {
        window.history.replaceState({}, document.title, appState?.returnTo || window.location.pathname);
      }}
      authorizationParams={{
        redirect_uri: window.location.origin,
        audience: auth0Audience,
        scope: "openid profile email",
      }}
    >
      <Auth0Root />
    </Auth0Provider>
  );
}

const root = ReactDOM.createRoot(document.getElementById('root'));
root.render(
  <React.StrictMode>
    <Root />
  </React.StrictMode>
);

reportWebVitals();
