import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "./App";

const COPYWRITER = { email: "ada@brand.com", permissions: ["analyse:claims"] };
const SENIOR_EDITOR = { email: "sam@brand.com", permissions: ["analyse:claims", "publish:product", "override:compliance", "edit:product-facts"] };
const BASIC_EDITOR = { email: "bo@brand.com", permissions: ["analyse:claims", "publish:product"] };

const PENDING = [
  { productId: "p-review", productName: "Wrap cardigan", finalDescription: "A wrap cardigan in a wool blend.", submittedAt: "2026-09-24T09:00:00Z", submittedByUserId: "auth0|ada", submittedByEmail: "ada@brand.com", market: "UK", aiStatus: "Ok", needsOverride: false, isOwnSubmission: false, canSignOff: true },
];
const ACTIVITY = [{ timestamp: "2026-09-25T10:00:00Z", action: "Submit", outcome: "Ok", productName: "Wrap cardigan", justification: null }];

const OVERVIEW = { generatedAt: "2026-09-25T10:00:00Z", drafts: 2, awaitingSignOff: 1, sentBack: 1, published: 4, topPhrases: [{ phrase: "eco-friendly", count: 6, category: "generic" }] };
const SUBMISSIONS = [
  { productId: "p-sent", productName: "Linen midi dress", market: "UK", status: "SentBack", submittedAt: "2026-09-24T09:00:00Z", updatedAt: "2026-09-25T09:00:00Z",
    sendBack: { reasonLabel: "Not substantiated", comment: "No certificate behind the organic claim.", at: "2026-09-25T09:00:00Z" } },
  { productId: "p-review", productName: "Wrap cardigan", market: "EU", status: "InReview", submittedAt: "2026-09-24T09:00:00Z", updatedAt: "2026-09-24T09:00:00Z", sendBack: null },
];
const DRAFTS = [{ productId: "p-draft", productName: "Denim jacket", market: "UK", savedAt: "2026-09-25T08:00:00Z", preview: "A sturdy jacket." }];

const PRODUCTS = {
  "p-sent": { id: "p-sent", name: "Linen midi dress", status: "SentBack", sendBack: SUBMISSIONS[0].sendBack },
  "p-review": { id: "p-review", name: "Wrap cardigan", status: "InReview", sendBack: null },
  "p-draft": { id: "p-draft", name: "Denim jacket", status: "Draft", sendBack: null },
};

let calls;

const json = (body, status = 200, headers = {}) =>
  Promise.resolve({ ok: status < 400, status, json: async () => body, blob: async () => new Blob(), headers: { get: (name) => headers[name] ?? null } });

let createdProducts;
let deletedDraftIds;

const install = () => {
  calls = [];
  createdProducts = {};
  deletedDraftIds = new Set();
  global.fetch = jest.fn((url, options = {}) => {
    const { pathname } = new URL(url);
    const method = options.method || "GET";
    const body = options.body ? JSON.parse(options.body) : null;
    calls.push({ method, path: pathname, body });

    if (pathname === "/api/my/overview") return json(OVERVIEW);
    if (pathname === "/api/my/submissions") return json(SUBMISSIONS);
    if (pathname === "/api/my/drafts") return json(DRAFTS.filter((d) => !deletedDraftIds.has(d.productId)));
    if (pathname === "/api/claims/pending-review") return json(PENDING);
    if (pathname === "/api/my/activity") return json(ACTIVITY);
    if (pathname === "/api/facts-requests") return json([{ id: 1, productId: "p-review", productName: "Wrap cardigan", note: "Please add the composition.", createdAt: "2026-09-23T10:00:00Z", requestedByEmail: "ada@brand.com", hasFacts: false }]);
    if (pathname === "/api/audit") return json([{ id: 1, timestamp: "2026-09-25T10:00:00Z", userEmail: "sam@brand.com", action: "Publish", outcome: "Ok", productName: "Wrap cardigan" }], 200, { "X-Total-Count": "1" });
    if (pathname === "/api/data-transfer/seed") return json({ title: "Not possible right now", detail: "Data imports only run in the Development environment." }, 409);
    if (pathname === "/api/claims/publish") return json({ success: true, overridden: false, message: "Published." });
    if (pathname === "/api/claims/mark-ready") {
      return json({ overallStatus: "READY_TO_PUBLISH_SUBJECT_TO_REVIEW", message: "Submitted for review." });
    }
    if (pathname === "/api/analyze") {
      if ((body.text || "").length > 5000) {
        return json({ error: "Input too long", message: "Product description must be 5,000 characters or fewer." }, 400);
      }
      const hit = (body.text || "").toLowerCase().includes("carbon neutral");
      const kept = hit && (body.issueDecisions || []).some((d) => d.issueId === "i-carbon" && d.userDecision === "KEPT_ORIGINAL_WITH_JUSTIFICATION");
      const timesOut = (body.text || "").toLowerCase().includes("ai-timeout-test");
      // real backend behaviour, a rules-only check never asks AI (Skipped), a check that did ask succeeds (Ok) unless the text is marked to fake a timeout
      const aiStatus = body.rulesOnly ? "Skipped" : timesOut ? "Timeout" : "Ok";
      return json({
        groupedFindings: hit ? [{ issueId: "i-carbon", category: "no_omission_information", severity: "high", matchedPatterns: ["carbon neutral"], suggestionText: aiStatus === "Ok" ? "Our whole range meets a verified emissions standard, detailed on the product page." : "" }] : [],
        overallStatus: "CHANGES_REQUIRED",
        aiStatus,
        submitAllowed: !hit || kept,
      });
    }
    if (pathname === "/api/products" && method === "POST") {
      createdProducts[body.name] = "p-new";
      return json({ id: "p-new", name: body.name }, 201);
    }
    const match = pathname.match(/^\/api\/products\/([^/]+)(?:\/(.+))?$/);
    if (match) {
      const [, id, sub] = match;
      const created = id === "p-new" ? { id, name: Object.keys(createdProducts).find((n) => createdProducts[n] === id) || "New product", status: "InReview", sendBack: null } : null;
      if (!sub && method === "GET") return json(PRODUCTS[id] || created);
      if (!sub && method === "PATCH") return json({ id, name: body.name });
      if (sub === "draft" && method === "GET") return id === "p-draft" ? json({ text: "A sturdy jacket.", market: "UK", savedAt: "2026-09-25T08:00:00Z" }) : id === "p-sent" ? json({ text: "Organic linen dress.", market: "UK", savedAt: "2026-09-25T09:00:00Z" }) : json({ title: "Not found" }, 404);
      if (sub === "draft" && method === "PUT") return json({ saved: true, savedAt: "2026-09-25T10:00:00Z" });
      if (sub === "draft" && method === "DELETE") {
        deletedDraftIds.add(id);
        return json(null, 204);
      }
      if (sub === "facts") return json({ status: "NoneOnFile", materials: [], certifications: [], origin: null });
      if (sub === "history") {
        return id === "p-review"
          ? json({ versions: [{ text: "A wrap cardigan in a wool blend.", market: "EU", decisions: [{ issueId: "i-1", phrase: "wool blend", critical: false, decision: "Kept", reason: "Blend confirmed by the mill." }] }] })
          : json({ versions: [{ text: "Our range features thoughtfully crafted pieces.", market: "UK", decisions: [] }] });
      }
    }
    return json({ title: "Not mocked" }, 404);
  });
};

// waits for the page's opening requests to finish inside act, so nothing updates after the test
const settle = () => act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });
const renderApp = async (user) => {
  const view = render(<App user={user} getToken={async () => "token"} onLogout={() => {}} />);
  await settle();
  return view;
};
const navItems = () => within(document.querySelector(".sidebar-nav")).getAllByRole("button").map((b) => b.textContent.trim());
const called = (path, method = "GET") => calls.filter((c) => c.path === path && c.method === method);

beforeEach(install);
afterEach(async () => {
  await settle();
  jest.restoreAllMocks();
});

describe("a Copywriter", () => {
  test("sees the Senior Editor screens too, but locked rather than hidden", async () => {
    jest.spyOn(window, "alert").mockImplementation(() => {});
    await renderApp(COPYWRITER);
    expect(navItems()).toContain("Verified Facts");
    expect(navItems()).toContain("Audit Trail");

    userEvent.click(screen.getByRole("button", { name: "Verified Facts" }));
    expect(window.alert).toHaveBeenCalledWith("Senior Editor only.");
    expect(screen.queryByText("Verified facts:", { exact: false })).not.toBeInTheDocument();
  });

  test("Activity shows their own real actions, not made-up ones", async () => {
    await renderApp(COPYWRITER);
    userEvent.click(screen.getByRole("button", { name: "Activity" }));
    expect(await screen.findByText("Submitted for review")).toBeInTheDocument();
    expect(screen.queryByText(/Sustainable Sneakers/)).not.toBeInTheDocument();
  });

  test("sees the full menu like everyone else, Product Lists hidden since that's not theirs", async () => {
    await renderApp(COPYWRITER);
    await waitFor(() => expect(navItems()).toContain("Overview"));

    const items = navItems();
    expect(items).toEqual(expect.arrayContaining(["Overview", "Product", "Product Inventory", "Reports", "Data Transfer", "Pending Review", "Verified Facts", "Audit Trail"]));
    expect(screen.getByRole("button", { name: "My Products" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Product Lists" })).not.toBeInTheDocument();
  });

  test("Overview shows their own numbers and what was sent back, and never asks for the team's", async () => {
    await renderApp(COPYWRITER);
    userEvent.click(screen.getByRole("button", { name: "Overview" }));

    expect(await screen.findByText("Sent back to you")).toBeInTheDocument();
    expect(screen.getByText("Not substantiated")).toBeInTheDocument();
    expect(screen.getByText("No certificate behind the organic claim.")).toBeInTheDocument();
    expect(screen.getByText("Awaiting sign-off")).toBeInTheDocument();
    expect(calls.some((c) => c.path.startsWith("/api/dashboard") || c.path === "/api/claims/recent")).toBe(false);
  });

  test("My Products lists drafts and submissions with their status, and Edit opens the draft", async () => {
    await renderApp(COPYWRITER);
    userEvent.click(screen.getByRole("button", { name: "My Products" }));

    const draftRow = (await screen.findByText("Denim jacket")).closest("tr");
    expect(within(draftRow).getByText("Draft")).toBeInTheDocument();
    expect(within(screen.getByText("Wrap cardigan").closest("tr")).getByText("In review")).toBeInTheDocument();
    expect(within(screen.getByText("Linen midi dress").closest("tr")).getByText("Sent back")).toBeInTheDocument();

    userEvent.click(within(draftRow).getByRole("button", { name: "Edit" }));
    await waitFor(() => expect(screen.getByLabelText("Product Description")).toHaveValue("A sturdy jacket."));
    expect(screen.getByLabelText("Product Name")).toHaveValue("Denim jacket");
    expect(screen.getByLabelText("Market")).toHaveValue("UK");
  });

  test("a sent-back product opens with the reason above the copy, and stays editable", async () => {
    await renderApp(COPYWRITER);
    userEvent.click(screen.getByRole("button", { name: "My Products" }));
    const row = (await screen.findByText("Linen midi dress")).closest("tr");
    userEvent.click(within(row).getByRole("button", { name: "Edit" }));

    expect(await screen.findByText(/Sent back by a Senior Editor: Not substantiated/)).toBeInTheDocument();
    expect(screen.getByText("No certificate behind the organic claim.")).toBeInTheDocument();
    expect(screen.getByLabelText("Product Description")).not.toHaveAttribute("readonly");
  });

  test("a product in review opens editable live, with Withdraw still offered as an alternative", async () => {
    await renderApp(COPYWRITER);
    userEvent.click(screen.getByRole("button", { name: "My Products" }));
    const row = (await screen.findByText("Wrap cardigan")).closest("tr");
    expect(within(row).getByRole("button", { name: "View" })).toBeInTheDocument();
    userEvent.click(within(row).getByRole("button", { name: "View" }));

    await waitFor(() => expect(screen.getByLabelText("Product Description")).toHaveValue("A wrap cardigan in a wool blend."));
    expect(screen.getByLabelText("Product Description")).not.toHaveAttribute("readonly");
    expect(screen.getByLabelText("Market")).toBeEnabled();
    expect(screen.getByText("In review.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Withdraw" })).toBeInTheDocument();
    // submit resubmits the live edit straight back into the queue, save draft stays disabled since there's no separate draft once it's in review
    expect(screen.getByRole("button", { name: "Submit" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Save draft" })).toBeDisabled();
  });

  test("why Submit won't do anything is said out loud only once they try, not while they are still writing", async () => {
    await renderApp(COPYWRITER);

    // nothing typed yet, so no nagging before they even try to submit
    expect(screen.queryByText("Write the product description first.")).not.toBeInTheDocument();

    userEvent.click(screen.getByRole("button", { name: "Submit" }));
    const reason = await screen.findByRole("alert");
    expect(reason).toHaveTextContent("Write the product description first.");

    // typing a name alone doesn't clear it, still needs a description
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    expect(screen.getByRole("alert")).toHaveTextContent("Write the product description first.");

    userEvent.type(screen.getByLabelText("Product Description"), "A soft linen dress for every day.");
    // The reason text changes to "still checking" while the automatic full check runs, then clears once it finishes.
    await waitFor(() => expect(screen.queryByRole("alert")).not.toBeInTheDocument(), { timeout: 3000 });
  });

  test("submitting successfully is confirmed where it can be seen, and stays editable live from there", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "New range");
    userEvent.type(screen.getByLabelText("Product Description"), "Our range features thoughtfully crafted pieces.");
    userEvent.type(screen.getByLabelText(/Product Category/), "Dresses");
    userEvent.type(screen.getByLabelText(/Product Subcategory/), "Midi dresses");
    userEvent.type(screen.getByLabelText(/Product Tags/), "Linen, Summer");
    await waitFor(() => expect(called("/api/analyze", "POST").length).toBeGreaterThan(0));

    userEvent.click(screen.getByRole("button", { name: "Submit" }));

    const confirmation = await screen.findByText("Submitted for review.");
    // Brought into focus, not left to render off-screen above the person's scroll position.
    expect(confirmation.closest('[role="status"]')).toHaveFocus();
    // still editable live from here, resubmitting doesn't lock it, the writer can keep refining it
    await waitFor(() => expect(screen.getByLabelText("Product Description")).not.toHaveAttribute("readonly"));
    expect(called("/api/claims/mark-ready", "POST")).toHaveLength(1);
    // The Product Details card is submitted alongside the description, so it can be checked and re-checked at publish the same way.
    expect(called("/api/claims/mark-ready", "POST")[0].body).toMatchObject({
      category: "Dresses",
      subcategory: "Midi dresses",
      tags: "Linen, Summer",
    });
  });

  test("a critical issue can be kept with a real reason, and only then does submitting work", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "New range");
    userEvent.type(screen.getByLabelText("Product Description"), "Our whole range is carbon neutral.");
    await screen.findByText("high");

    userEvent.click(screen.getByRole("button", { name: "Submit" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Resolve the critical issue(s) before this can be submitted.");

    userEvent.click(screen.getByRole("button", { name: "Keep with a reason" }));
    const reasonBox = screen.getByPlaceholderText(/at least 15 characters/);
    const confirm = screen.getByRole("button", { name: "Confirm keep" });

    userEvent.type(reasonBox, "too short");
    expect(confirm).toBeDisabled();

    userEvent.clear(reasonBox);
    userEvent.type(reasonBox, "Certified under scheme ref 4471");
    expect(confirm).toBeEnabled();
    userEvent.click(confirm);

    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    userEvent.click(screen.getByRole("button", { name: "Submit" }));

    await screen.findByText("Submitted for review.");
    expect(called("/api/claims/mark-ready", "POST")).toHaveLength(1);
    expect(called("/api/claims/mark-ready", "POST")[0].body.issueDecisions).toEqual([
      expect.objectContaining({ issueId: "i-carbon", userDecision: "KEPT_ORIGINAL_WITH_JUSTIFICATION", userJustification: "Certified under scheme ref 4471" }),
    ]);
  });

  test("what they type is checked with the product name and the market, rules first then the AI once typing pauses", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    userEvent.selectOptions(screen.getByLabelText("Market"), "EU");
    userEvent.type(screen.getByLabelText("Product Description"), "A soft linen dress for every day.");

    // fast rules-only check fires first, for instant feedback while still typing
    await waitFor(() => expect(called("/api/analyze", "POST").some((c) => c.body.rulesOnly === true)).toBe(true));
    // the full check (rules + AI) runs on its own once typing pauses
    await waitFor(() => expect(called("/api/analyze", "POST").some((c) => c.body.rulesOnly === false)).toBe(true), { timeout: 3000 });

    const aiChecks = called("/api/analyze", "POST").filter((c) => c.body.rulesOnly === false);
    expect(aiChecks).toHaveLength(1);
    expect(aiChecks[0].body).toMatchObject({ market: "EU", productName: "Linen dress" });
  });

  test("what they type in the Product Details card (category, subcategory, tags) is checked too", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    userEvent.type(screen.getByLabelText("Product Description"), "A soft linen dress for every day.");
    userEvent.type(screen.getByLabelText(/Product Category/), "Eco Dresses");
    userEvent.type(screen.getByLabelText(/Product Subcategory/), "Midi dresses");
    userEvent.type(screen.getByLabelText(/Product Tags/), "Linen, Summer");

    await waitFor(() => expect(called("/api/analyze", "POST").length).toBeGreaterThan(0));
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 1800)); });

    const checks = called("/api/analyze", "POST");
    expect(checks.at(-1).body).toMatchObject({
      productCategory: "Eco Dresses",
      productSubcategory: "Midi dresses",
      productTags: "Linen, Summer",
    });
  });

  test("the product editor starts with no made-up content, and no button that does nothing", async () => {
    await renderApp(COPYWRITER);

    // the category and tags are optional and start empty
    expect(screen.getByLabelText(/Product Category/)).toHaveValue("");
    expect(screen.getByLabelText(/Product Subcategory/)).toHaveValue("");
    expect(screen.getByLabelText(/Product Tags/)).toHaveValue("");
    expect(screen.queryByText("Sustainable")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /upload .txt file/i })).not.toBeInTheDocument();
  });

  test("the AI is always told this is fashion copy, even with the category left empty", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    userEvent.type(screen.getByLabelText("Product Description"), "A soft linen dress for every day.");
    await waitFor(() => expect(called("/api/analyze", "POST").length).toBeGreaterThan(0));
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 1800)); });

    expect(called("/api/analyze", "POST").every((c) => c.body.industry === "fashion")).toBe(true);
  });

  test("the full check asks the AI once, for that exact text", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    userEvent.type(screen.getByLabelText("Product Description"), "A soft linen dress for every day.");

    // the full check (rules + AI) runs on its own after a pause in typing
    await waitFor(() => expect(called("/api/analyze", "POST").some((c) => c.body.rulesOnly === false)).toBe(true), { timeout: 3000 });
    const ai = called("/api/analyze", "POST").filter((c) => c.body.rulesOnly === false);
    expect(ai).toHaveLength(1);
    expect(ai[0].body).toMatchObject({ productName: "Linen dress", market: "UK" });
  });

  // real bug found in testing, asking for AI got treated as it actually working, so a timed-out call still showed the fallback text as "Compliant rewrite" and let it be applied as if real
  test("Apply suggestion only turns on once the AI actually succeeds, not just because it was asked", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    userEvent.type(screen.getByLabelText("Product Description"), "Our whole range is carbon neutral.");
    await waitFor(() => expect(called("/api/analyze", "POST").length).toBeGreaterThan(0));

    // rules-only live check, a finding shows up but nothing claims to be a compliant rewrite yet
    expect(await screen.findByText("Rule guidance")).toBeInTheDocument();
    expect(screen.queryByText("Compliant rewrite")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Apply suggestion" })).toBeDisabled();

    // the full check (rules + AI) runs on its own after a pause in typing
    await waitFor(() => expect(called("/api/analyze", "POST").some((c) => c.body.rulesOnly === false)).toBe(true), { timeout: 3000 });

    // AI actually succeeded here (aiStatus "Ok"), so it really is a compliant rewrite and applying it is safe
    expect(await screen.findByText("Compliant rewrite")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Apply suggestion" })).toBeEnabled();
  });

  // real copywriters write ASOS-style copy, short "·" separated fragments on one line, fixing one must never touch its neighbours
  test("applying a suggestion to ASOS-style bulleted copy only touches the flagged fragment", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    fireEvent.change(screen.getByLabelText("Product Description"), {
      target: { value: "Round neck · Long sleeves · Our whole range is carbon neutral · Machine wash according to label" },
    });
    await waitFor(() => expect(called("/api/analyze", "POST").length).toBeGreaterThan(0));

    // the full check (rules + AI) runs on its own after a pause in typing
    await waitFor(() => expect(called("/api/analyze", "POST").some((c) => c.body.rulesOnly === false)).toBe(true), { timeout: 3000 });
    expect(await screen.findByRole("button", { name: "Apply suggestion" })).toBeEnabled();

    userEvent.click(screen.getByRole("button", { name: "Apply suggestion" }));

    await waitFor(() => {
      const value = screen.getByLabelText("Product Description").value;
      expect(value).toContain("Round neck · Long sleeves ·");
      expect(value).toContain("· Machine wash according to label");
      expect(value).not.toContain("carbon neutral");
      expect(value).toContain("Our whole range meets a verified emissions standard, detailed on the product page.");
      // The bullet spacing either side of the replaced fragment must survive, not be squashed together.
      expect(value).not.toMatch(/·(?!\s)Our whole range/);
      expect(value).not.toMatch(/product page\.(?!\s)·/);
    });
  });

  test("a timed-out AI call still shows rule guidance, not a rewrite dressed up as one", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    userEvent.type(screen.getByLabelText("Product Description"), "ai-timeout-test our whole range is carbon neutral.");
    await waitFor(() => expect(called("/api/analyze", "POST").length).toBeGreaterThan(0));

    // the full check (rules + AI) runs on its own after a pause in typing
    await waitFor(() => expect(called("/api/analyze", "POST").some((c) => c.body.rulesOnly === false)).toBe(true), { timeout: 3000 });

    expect(await screen.findByText(/AI rewrite unavailable \(it took too long to respond\)/)).toBeInTheDocument();
    expect(screen.getByText("Rule guidance")).toBeInTheDocument();
    expect(screen.queryByText("Compliant rewrite")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Apply suggestion" })).toBeDisabled();
  });

  // real bug, the server rejects text over 5,000 characters with a useful message, but the frontend used to drop it and show a fake "Connection Error" telling the writer to start the backend, which made no sense for a validation error
  test("a description over the character limit says why, not a fake connection error", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    const tooLong = "Soft and warm. ".repeat(400); // well past 5,000 characters
    fireEvent.change(screen.getByLabelText("Product Description"), { target: { value: tooLong } });

    expect(await screen.findByText(/over the limit, checks will be rejected/)).toBeInTheDocument();
    await waitFor(() => expect(called("/api/analyze", "POST").length).toBeGreaterThan(0));

    expect(await screen.findByText("Input rejected")).toBeInTheDocument();
    expect(screen.getByText("Product description must be 5,000 characters or fewer.")).toBeInTheDocument();
    expect(screen.queryByText(/Service connection issue/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Start API with/)).not.toBeInTheDocument();
  });

  test("the draft saves itself: the product is created once and the text saved with its market", async () => {
    await renderApp(COPYWRITER);
    userEvent.type(screen.getByLabelText("Product Name"), "Linen dress");
    userEvent.type(screen.getByLabelText("Product Description"), "A soft linen dress for every day.");

    await waitFor(() => expect(called("/api/products/p-new/draft", "PUT")).toHaveLength(1), { timeout: 4000 });
    expect(called("/api/products", "POST")).toHaveLength(1);
    expect(called("/api/products/p-new/draft", "PUT")[0].body).toEqual({ text: "A soft linen dress for every day.", market: "UK" });
    expect(await screen.findByText(/Draft saved at/)).toBeInTheDocument();
  });

  test("a draft can be discarded straight from My Products, without opening it first", async () => {
    const confirm = jest.spyOn(window, "confirm").mockReturnValue(true);
    await renderApp(COPYWRITER);
    userEvent.click(screen.getByRole("button", { name: "My Products" }));
    const draftRow = (await screen.findByText("Denim jacket")).closest("tr");

    userEvent.click(within(draftRow).getByRole("button", { name: "Discard this draft: Denim jacket" }));
    expect(confirm).toHaveBeenCalledWith(expect.stringMatching(/Discard this draft/));
    await waitFor(() => expect(called("/api/products/p-draft/draft", "DELETE")).toHaveLength(1));
    await waitFor(() => expect(screen.queryByText("Denim jacket")).not.toBeInTheDocument());
  });

  test("declining the confirm on a list discard changes nothing", async () => {
    jest.spyOn(window, "confirm").mockReturnValue(false);
    await renderApp(COPYWRITER);
    userEvent.click(screen.getByRole("button", { name: "My Products" }));
    const draftRow = (await screen.findByText("Denim jacket")).closest("tr");

    userEvent.click(within(draftRow).getByRole("button", { name: "Discard this draft: Denim jacket" }));
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    expect(screen.getByText("Denim jacket")).toBeInTheDocument();
  });

  test("Discard asks first, and does nothing if they keep editing", async () => {
    const confirm = jest.spyOn(window, "confirm").mockReturnValue(false);
    await renderApp(COPYWRITER);
    userEvent.click(screen.getByRole("button", { name: "My Products" }));
    userEvent.click(within((await screen.findByText("Denim jacket")).closest("tr")).getByRole("button", { name: "Edit" }));
    await waitFor(() => expect(screen.getByLabelText("Product Description")).toHaveValue("A sturdy jacket."));

    userEvent.click(screen.getByRole("button", { name: "Discard" }));
    expect(confirm).toHaveBeenCalledWith(expect.stringMatching(/Discard this draft/));
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    expect(screen.getByLabelText("Product Description")).toHaveValue("A sturdy jacket.");
  });
});

describe("a Senior Editor", () => {
  test("also gets Verified Facts and the Audit Trail, which a Copywriter never does", async () => {
    await renderApp(SENIOR_EDITOR);
    await waitFor(() => expect(navItems()).toContain("Verified Facts"));
    expect(navItems()).toEqual(expect.arrayContaining(["Verified Facts", "Audit Trail"]));
  });

  test("reviews a submission, sees what the writer decided, and publishes it after confirming", async () => {
    jest.spyOn(window, "confirm").mockReturnValue(true);
    await renderApp(SENIOR_EDITOR);
    userEvent.click(screen.getByRole("button", { name: "Pending Review" }));
    const row = (await screen.findByText("Wrap cardigan")).closest("tr");
    expect(within(row).getByText("ada@brand.com")).toBeInTheDocument();
    userEvent.click(within(row).getByRole("button", { name: "Review" }));

    expect(await screen.findByText("Blend confirmed by the mill.")).toBeInTheDocument();
    userEvent.click(screen.getByRole("button", { name: "Publish" }));

    await waitFor(() => expect(called("/api/claims/publish", "POST")).toHaveLength(1));
    expect(called("/api/claims/publish", "POST")[0].body).toEqual({ productId: "p-review" });
    expect(await screen.findByText("Wrap cardigan was published.")).toBeInTheDocument();
  });

  test("with the full permissions the review offers to edit the facts, and it opens that product's facts", async () => {
    await renderApp(SENIOR_EDITOR);
    userEvent.click(screen.getByRole("button", { name: "Pending Review" }));
    userEvent.click(within((await screen.findByText("Wrap cardigan")).closest("tr")).getByRole("button", { name: "Review" }));
    userEvent.click(await screen.findByRole("button", { name: "Edit facts" }));

    expect(await screen.findByText("Verified facts: Wrap cardigan")).toBeInTheDocument();
    expect(screen.getByLabelText(/Why are the facts changing/)).toBeEnabled();
  });

  test("with only the Senior Editor permission they cannot change facts or override, and are told why", async () => {
    await renderApp(BASIC_EDITOR);
    userEvent.click(screen.getByRole("button", { name: "Pending Review" }));
    userEvent.click(within((await screen.findByText("Wrap cardigan")).closest("tr")).getByRole("button", { name: "Review" }));

    expect(await screen.findByText(/don't have permission to change the verified facts/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Edit facts" })).not.toBeInTheDocument();
  });

  test("a data import that is refused says why, instead of doing nothing", async () => {
    await renderApp(SENIOR_EDITOR);
    userEvent.click(screen.getByRole("button", { name: "Data Transfer" }));
    userEvent.click(await screen.findByRole("button", { name: "Run Seed Import" }));

    expect(await screen.findByText("Data imports only run in the Development environment.")).toBeInTheDocument();
  });

  test("the Audit Trail lists real rows, and Activity shows their own actions", async () => {
    await renderApp(SENIOR_EDITOR);
    userEvent.click(screen.getByRole("button", { name: "Audit Trail" }));
    expect(await screen.findByText("Published")).toBeInTheDocument();
    expect(screen.getByText("sam@brand.com")).toBeInTheDocument();

    userEvent.click(screen.getByRole("button", { name: "Activity" }));
    expect(await screen.findByText("Submitted for review")).toBeInTheDocument();
    expect(screen.queryByText(/Sustainable Sneakers/)).not.toBeInTheDocument();
  });

  test("keeps the team screens and never asks for the Copywriter's own lists", async () => {
    await renderApp(SENIOR_EDITOR);
    await waitFor(() => expect(navItems()).toContain("Overview"));

    expect(navItems()).toEqual(expect.arrayContaining(["Product", "Overview", "Reports", "Data Transfer", "Pending Review", "Verified Facts", "Audit Trail"]));
    expect(screen.getByRole("button", { name: "Product Lists" })).toBeInTheDocument();
    // their own activity is real for both roles, but the Copywriter's counts and lists aren't theirs to ask for
    expect(calls.some((c) => ["/api/my/overview", "/api/my/submissions", "/api/my/drafts"].includes(c.path))).toBe(false);
  });

  test("gets the same editor with the market, the status lock and the save line", async () => {
    await renderApp(SENIOR_EDITOR);
    expect(screen.getByLabelText("Market")).toBeEnabled();
    expect(screen.getByRole("button", { name: "Save draft" })).toBeEnabled();
  });
});
