export const auth0Domain   = process.env.REACT_APP_AUTH0_DOMAIN   || "";
export const auth0ClientId = process.env.REACT_APP_AUTH0_CLIENT_ID || "";
export const auth0Audience = process.env.REACT_APP_AUTH0_AUDIENCE  || "https://greenclaims-api";

export const isAuth0Configured = Boolean(auth0Domain && auth0ClientId);

// uses Auth0's built-in RBAC "permissions" claim, not a custom one, the custom one doesn't reliably fill in inside Post-Login Actions but the built-in one always does
export const PERMISSIONS_CLAIM = "permissions";
