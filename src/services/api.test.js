import { ApiError, createApi } from "./api";

const respond = (status, body, headers = {}) => ({
  ok: status < 400,
  status,
  json: async () => body,
  blob: async () => new Blob(["csv"]),
  headers: { get: (name) => headers[name] ?? null },
});

const setup = (response = respond(200, {}), extra = {}) => {
  const fetchImpl = jest.fn(async () => response);
  const api = createApi({ baseUrl: "http://api", getAuthHeaders: async (h) => ({ ...h, Authorization: "Bearer t" }), fetchImpl, ...extra });
  return { api, fetchImpl };
};

const lastCall = (fetchImpl) => {
  const [url, options] = fetchImpl.mock.calls.at(-1);
  return { url, method: options.method, body: options.body ? JSON.parse(options.body) : null, headers: options.headers };
};

describe("requests", () => {
  test("carry the sign-in token, and a JSON body only when there is one", async () => {
    const { api, fetchImpl } = setup();
    await api.getMyOverview();
    expect(lastCall(fetchImpl).headers).toEqual({ Authorization: "Bearer t" });
    await api.createProduct("Linen dress");
    expect(lastCall(fetchImpl)).toMatchObject({ url: "http://api/api/products", method: "POST", body: { name: "Linen dress" } });
    expect(lastCall(fetchImpl).headers["Content-Type"]).toBe("application/json");
  });

  test("a refusal becomes an error with the server's own words and its status", async () => {
    const { api } = setup(respond(409, { detail: "This product is in review, so its copy is locked." }));
    await expect(api.saveDraft("p", { text: "x", market: "UK" })).rejects.toMatchObject({ name: "ApiError", status: 409, message: "This product is in review, so its copy is locked." });
  });

  test("an older-style message answer is used too", async () => {
    const { api } = setup(respond(400, { message: "Product name is required." }));
    await expect(api.createProduct("")).rejects.toThrow("Product name is required.");
  });

  test("a 401 tells the app to send the person back to log in, and still raises the error", async () => {
    const onUnauthorized = jest.fn();
    const { api } = setup(respond(401, { title: "Sign in required" }), { onUnauthorized });
    await expect(api.getMySubmissions()).rejects.toBeInstanceOf(ApiError);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  test("a 403 is not a sign-in problem", async () => {
    const onUnauthorized = jest.fn();
    const { api } = setup(respond(403, { detail: "You do not have access to this." }), { onUnauthorized });
    await expect(api.getProduct("p")).rejects.toMatchObject({ status: 403 });
    expect(onUnauthorized).not.toHaveBeenCalled();
  });
});

describe("drafts", () => {
  test("a product with no draft is null, not an error", async () => {
    const { api } = setup(respond(404, { title: "Not found" }));
    await expect(api.getDraft("p")).resolves.toBeNull();
  });

  test("any other failure while reading a draft still raises", async () => {
    const { api } = setup(respond(403, { detail: "no" }));
    await expect(api.getDraft("p")).rejects.toMatchObject({ status: 403 });
  });

  test("saving sends the text and its market", async () => {
    const { api, fetchImpl } = setup();
    await api.saveDraft("p-1", { text: "Soft linen.", market: "EU" });
    expect(lastCall(fetchImpl)).toMatchObject({ url: "http://api/api/products/p-1/draft", method: "PUT", body: { text: "Soft linen.", market: "EU" } });
  });
});

describe("a Senior Editor's actions", () => {
  test("publishing sends an override reason only when there is one", async () => {
    const { api, fetchImpl } = setup();
    await api.publish("p-1");
    expect(lastCall(fetchImpl).body).toEqual({ productId: "p-1" });
    await api.publish("p-1", "Certificate checked today.");
    expect(lastCall(fetchImpl).body).toEqual({ productId: "p-1", overrideReason: "Certificate checked today." });
  });

  test("sending back sends the reason category and the comment", async () => {
    const { api, fetchImpl } = setup();
    await api.sendBack("p-1", "not_substantiated", "No certificate behind it.");
    expect(lastCall(fetchImpl)).toMatchObject({ url: "http://api/api/claims/send-back", body: { productId: "p-1", reasonCategory: "not_substantiated", comment: "No certificate behind it." } });
  });

  test("saving facts always sends the reason", async () => {
    const { api, fetchImpl } = setup();
    await api.saveFacts("p-1", { materials: [{ material: "cotton", percentage: 100 }], certifications: [], origin: "Portugal", reason: "Checked the sheet." });
    expect(lastCall(fetchImpl)).toMatchObject({ method: "PUT", url: "http://api/api/products/p-1/facts", body: { reason: "Checked the sheet.", origin: "Portugal" } });
  });

  test("an audit page asks for its window, and reports how many rows there are in all", async () => {
    const { api, fetchImpl } = setup(respond(200, [{ id: 1 }], { "X-Total-Count": "57" }));
    const page = await api.getAudit({ from: "2026-09-01T00:00:00", skip: 25, take: 25 });

    expect(lastCall(fetchImpl).url).toBe("http://api/api/audit?from=2026-09-01T00%3A00%3A00&skip=25&take=25");
    expect(page).toEqual({ rows: [{ id: 1 }], total: 57 });
  });

  test("the export uses the same filters and returns the file", async () => {
    const { api, fetchImpl } = setup();
    const file = await api.exportAudit({ from: "2026-09-01T00:00:00", to: "2026-09-30T23:59:59" });
    expect(lastCall(fetchImpl).url).toBe("http://api/api/audit/export?from=2026-09-01T00%3A00%3A00&to=2026-09-30T23%3A59%3A59");
    expect(file).toBeInstanceOf(Blob);
  });

  test("a refused export raises the server's reason", async () => {
    const { api } = setup(respond(403, { detail: "You do not have access to this." }));
    await expect(api.exportAudit()).rejects.toMatchObject({ status: 403, message: "You do not have access to this." });
  });
});
