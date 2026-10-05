// every backend call lives here. each one throws ApiError with the status and body (a 401 also calls onUnauthorized), and uses the server's own message over a generic fallback

export class ApiError extends Error {
  constructor(status, body, fallbackMessage) {
    super((body && (body.detail || body.message)) || fallbackMessage || `Request failed (${status})`);
    this.name = "ApiError";
    this.status = status;
    this.body = body || null;
  }
}

const readBody = async (response) => {
  try {
    return await response.json();
  } catch {
    return null;
  }
};

export function createApi({ baseUrl, getAuthHeaders, onUnauthorized, fetchImpl }) {
  const doFetch = fetchImpl || ((...args) => fetch(...args));

  const request = async (path, { method = "GET", body, withHeaders = false } = {}) => {
    const headers = await getAuthHeaders(body ? { "Content-Type": "application/json" } : {});
    const response = await doFetch(`${baseUrl}${path}`, {
      method,
      headers,
      ...(body ? { body: JSON.stringify(body) } : {}),
    });
    const data = await readBody(response);
    if (!response.ok) {
      if (response.status === 401 && onUnauthorized) onUnauthorized();
      throw new ApiError(response.status, data);
    }
    return withHeaders ? { data, headers: response.headers } : data;
  };

  const query = (params) => {
    const parts = Object.entries(params).filter(([, v]) => v !== undefined && v !== null && v !== "").map(([k, v]) => `${k}=${encodeURIComponent(v)}`);
    return parts.length ? `?${parts.join("&")}` : "";
  };

  return {
    // A Copywriter's own numbers, and the phrases they are flagged for most.
    getMyOverview: () => request("/api/my/overview"),
    // Their own submissions with status and any send-back reason.
    getMySubmissions: () => request("/api/my/submissions"),
    // Their own saved, unsubmitted drafts.
    getMyDrafts: () => request("/api/my/drafts"),

    getProduct: (id) => request(`/api/products/${id}`),
    createProduct: (name) => request("/api/products", { method: "POST", body: { name } }),
    renameProduct: (id, name) => request(`/api/products/${id}`, { method: "PATCH", body: { name } }),

    // A product with no saved draft answers 404, which here just means "none".
    getDraft: async (id) => {
      try {
        return await request(`/api/products/${id}/draft`);
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null;
        throw error;
      }
    },
    saveDraft: (id, { text, market }) => request(`/api/products/${id}/draft`, { method: "PUT", body: { text, market } }),
    deleteDraft: (id) => request(`/api/products/${id}/draft`, { method: "DELETE" }),

    withdraw: (productId) => request("/api/claims/withdraw", { method: "POST", body: { productId } }),
    // every version submitted, oldest first, used to show what's in review or published, read only
    getHistory: (id) => request(`/api/products/${id}/history`),

    // What a Senior Editor has verified, read-only, and the note that asks for more.
    getFacts: (id) => request(`/api/products/${id}/facts`),
    requestFacts: (id, note) => request(`/api/products/${id}/facts-requests`, { method: "POST", body: { note } }),

    // What the person has done themselves (from the audit trail), for either role.
    getMyActivity: () => request("/api/my/activity"),

    // Senior Editor

    getPendingReviews: () => request("/api/claims/pending-review"),
    // publishes a submission. the server re-checks it, and with a reason it can publish past a block
    publish: (productId, overrideReason) =>
      request("/api/claims/publish", { method: "POST", body: { productId, ...(overrideReason ? { overrideReason } : {}) } }),
    sendBack: (productId, reasonCategory, comment) =>
      request("/api/claims/send-back", { method: "POST", body: { productId, reasonCategory, comment } }),
    // Replaces the verified facts. A reason is required and the change is recorded with before and after.
    saveFacts: (id, { materials, certifications, origin, reason }) =>
      request(`/api/products/${id}/facts`, { method: "PUT", body: { materials, certifications, origin, reason } }),
    getFactsRequests: () => request("/api/facts-requests"),
    // One page of the audit trail and how many rows there are in all for these filters.
    getAudit: async ({ from, to, skip, take } = {}) => {
      const { data, headers } = await request(`/api/audit${query({ from, to, skip, take })}`, { withHeaders: true });
      return { rows: data || [], total: Number(headers.get("X-Total-Count")) || 0 };
    },
    // The same rows as a file, for the same filters.
    exportAudit: async ({ from, to } = {}) => {
      const headers = await getAuthHeaders();
      const response = await doFetch(`${baseUrl}/api/audit/export${query({ from, to })}`, { headers });
      if (!response.ok) {
        if (response.status === 401 && onUnauthorized) onUnauthorized();
        throw new ApiError(response.status, await readBody(response));
      }
      return response.blob();
    },
  };
}
