import { act, renderHook } from "@testing-library/react";
import { ApiError } from "../services/api";
import { AUTOSAVE_DELAY_MS, useMyProduct } from "./useMyProduct";

const makeApi = (over = {}) => ({
  getProduct: jest.fn(async (id) => ({ id, name: "Wrap cardigan", status: "Draft", sendBack: null })),
  createProduct: jest.fn(async (name) => ({ id: "p-1", name })),
  renameProduct: jest.fn(async () => ({})),
  getDraft: jest.fn(async () => ({ text: "A soft wrap cardigan.", market: "EU", savedAt: "2026-09-25T10:00:00Z" })),
  saveDraft: jest.fn(async () => ({ saved: true })),
  deleteDraft: jest.fn(async () => null),
  withdraw: jest.fn(async () => ({})),
  getHistory: jest.fn(async () => ({ versions: [{ text: "Submitted copy.", market: "UK" }] })),
  getFacts: jest.fn(async () => ({ status: "NoneOnFile", materials: [], certifications: [], origin: null })),
  requestFacts: jest.fn(async () => ({})),
  ...over,
});

const flush = async (ms = 0) => {
  await act(async () => {
    jest.advanceTimersByTime(ms);
    for (let i = 0; i < 30; i += 1) await Promise.resolve();
  });
};

const setup = (api = makeApi(), form = { name: "", text: "", market: "UK" }) => {
  const view = renderHook((props) => useMyProduct({ api, ...props }), { initialProps: form });
  return { api, ...view, type: (next) => view.rerender({ ...form, ...next }) };
};

beforeEach(() => jest.useFakeTimers());
afterEach(() => jest.useRealTimers());

describe("autosave", () => {
  test("nothing is saved until the writer stops typing, then the product is created once and the text saved", async () => {
    const { api, result, type } = setup();
    type({ name: "Linen dress", text: "Our soft linen dress." });
    await flush(AUTOSAVE_DELAY_MS - 100);
    expect(api.createProduct).not.toHaveBeenCalled();

    await flush(200);
    expect(api.createProduct).toHaveBeenCalledTimes(1);
    expect(api.createProduct).toHaveBeenCalledWith("Linen dress");
    expect(api.saveDraft).toHaveBeenCalledWith("p-1", { text: "Our soft linen dress.", market: "UK" });
    expect(result.current.productId).toBe("p-1");
    expect(result.current.save.state).toBe("saved");
    expect(result.current.dirty).toBe(false);
  });

  test("with no name nothing is saved, and a manual save says why", async () => {
    const { api, result, type } = setup();
    type({ text: "Our soft linen dress." });
    await flush(AUTOSAVE_DELAY_MS + 100);
    expect(api.createProduct).not.toHaveBeenCalled();
    expect(result.current.dirty).toBe(true);

    await act(async () => { await result.current.actions.saveNow(); });
    expect(result.current.notice).toMatchObject({ tone: "error", text: "Give the product a name to save a draft." });
  });

  test("a rename is sent, and a second edit never creates a second product", async () => {
    const { api, type } = setup();
    type({ name: "Linen dress", text: "Our soft linen dress." });
    await flush(AUTOSAVE_DELAY_MS + 100);
    type({ name: "Linen midi dress", text: "Our soft linen dress." });
    await flush(AUTOSAVE_DELAY_MS + 100);

    expect(api.createProduct).toHaveBeenCalledTimes(1);
    expect(api.renameProduct).toHaveBeenCalledWith("p-1", "Linen midi dress");
    expect(api.saveDraft).toHaveBeenCalledTimes(2);
  });

  test("a market change is saved too", async () => {
    const { api, type } = setup();
    type({ name: "Dress", text: "A soft dress.", market: "UK" });
    await flush(AUTOSAVE_DELAY_MS + 100);
    type({ name: "Dress", text: "A soft dress.", market: "EU" });
    await flush(AUTOSAVE_DELAY_MS + 100);
    expect(api.saveDraft).toHaveBeenLastCalledWith("p-1", { text: "A soft dress.", market: "EU" });
  });

  test("a failed save says so and does not pretend it worked", async () => {
    const api = makeApi({ saveDraft: jest.fn(async () => { throw new ApiError(500, null, "The server could not answer."); }) });
    const { result, type } = setup(api);
    type({ name: "Dress", text: "A soft dress." });
    await flush(AUTOSAVE_DELAY_MS + 100);

    expect(result.current.save).toMatchObject({ state: "error", message: "The server could not answer." });
    expect(result.current.dirty).toBe(true);
  });

  test("when it went into review meanwhile, it reloads as it now is and says why", async () => {
    const api = makeApi({
      saveDraft: jest.fn(async () => { throw new ApiError(409, { detail: "This product is in review, so its copy is locked. Withdraw it to make changes." }); }),
      getProduct: jest.fn(async () => ({ id: "p-1", name: "Dress", status: "InReview", sendBack: null })),
    });
    const { result, type } = setup(api);
    type({ name: "Dress", text: "A soft dress." });
    await flush(AUTOSAVE_DELAY_MS + 100);

    expect(result.current.status).toBe("InReview");
    // the draft save was correctly refused (409 is real), but the writer owns this product so once reloaded as InReview they can still edit it live
    expect(result.current.editable).toBe(true);
    expect(result.current.notice.text).toMatch(/locked/);
  });

  test("a locked product is never saved", async () => {
    const api = makeApi({ getProduct: jest.fn(async () => ({ id: "p-7", name: "Cardigan", status: "Published", sendBack: null })) });
    const { result, type } = setup(api);
    await act(async () => { await result.current.actions.open("p-7"); });
    type({ name: "Cardigan", text: "Edited after publishing." });
    await flush(AUTOSAVE_DELAY_MS + 100);
    expect(api.saveDraft).not.toHaveBeenCalled();
  });
});

describe("opening a product", () => {
  test("a draft loads its name, text and market, and counts as already saved", async () => {
    const { api, result, type } = setup();
    let loaded;
    await act(async () => { loaded = await result.current.actions.open("p-7"); });

    expect(loaded).toEqual({ name: "Wrap cardigan", text: "A soft wrap cardigan.", market: "EU", status: "Draft" });
    expect(result.current.productId).toBe("p-7");
    expect(result.current.save.state).toBe("saved");

    type({ name: "Wrap cardigan", text: "A soft wrap cardigan.", market: "EU" });
    await flush(AUTOSAVE_DELAY_MS + 100);
    expect(api.saveDraft).not.toHaveBeenCalled();
  });

  test("a sent-back product carries its reason and stays editable", async () => {
    const api = makeApi({
      getProduct: jest.fn(async (id) => ({ id, name: "Cardigan", status: "SentBack", sendBack: { reasonLabel: "Not substantiated", comment: "No certificate." } })),
    });
    const { result } = setup(api);
    await act(async () => { await result.current.actions.open("p-7"); });

    expect(result.current.editable).toBe(true);
    expect(result.current.sendBack).toMatchObject({ reasonLabel: "Not substantiated", comment: "No certificate." });
  });

  test("a product in review shows its submitted text, editable live by the writer who owns it", async () => {
    const api = makeApi({ getProduct: jest.fn(async (id) => ({ id, name: "Cardigan", status: "InReview", sendBack: null })) });
    const { result } = setup(api);
    let loaded;
    await act(async () => { loaded = await result.current.actions.open("p-7"); });

    expect(loaded.text).toBe("Submitted copy.");
    expect(result.current.submittedText).toBe("Submitted copy.");
    expect(result.current.editable).toBe(true);
    expect(api.getDraft).not.toHaveBeenCalled();
  });

  test("someone else's product is a plain error, and the form is left alone", async () => {
    const api = makeApi({ getProduct: jest.fn(async () => { throw new ApiError(403, { detail: "You can only open your own products." }); }) });
    const { result } = setup(api);
    let loaded;
    await act(async () => { loaded = await result.current.actions.open("p-9"); });

    expect(loaded).toBeNull();
    expect(result.current.loading.error.status).toBe(403);
    expect(result.current.productId).toBeNull();
  });
});

describe("withdraw and discard", () => {
  test("withdrawing puts it back as an editable draft with its text, and says so", async () => {
    let status = "InReview";
    const api = makeApi({
      getProduct: jest.fn(async (id) => ({ id, name: "Cardigan", status, sendBack: null })),
      withdraw: jest.fn(async () => { status = "Draft"; }),
    });
    const { result } = setup(api);
    await act(async () => { await result.current.actions.open("p-7"); });
    expect(result.current.editable).toBe(true); // in review, but still the writer's own, already editable

    let loaded;
    await act(async () => { loaded = await result.current.actions.withdraw(); });
    expect(api.withdraw).toHaveBeenCalledWith("p-7");
    expect(loaded).toMatchObject({ status: "Draft", text: "A soft wrap cardigan." });
    expect(result.current.editable).toBe(true);
    expect(result.current.notice).toMatchObject({ tone: "ok", text: "Withdrawn." });
  });

  test("a refused withdrawal says why and changes nothing", async () => {
    const api = makeApi({ withdraw: jest.fn(async () => { throw new ApiError(409, { detail: "Only a submission that is waiting for sign-off can be withdrawn." }); }) });
    const { result } = setup(api);
    await act(async () => { await result.current.actions.open("p-7"); });
    let loaded;
    await act(async () => { loaded = await result.current.actions.withdraw(); });

    expect(loaded).toBeNull();
    expect(result.current.notice).toMatchObject({ tone: "error", text: "Only a submission that is waiting for sign-off can be withdrawn." });
  });

  test("discard deletes the saved draft, and does nothing on the server for a product that was never saved", async () => {
    const { api, result } = setup();
    await act(async () => { expect(await result.current.actions.discard()).toBe(true); });
    expect(api.deleteDraft).not.toHaveBeenCalled();

    await act(async () => { await result.current.actions.open("p-7"); });
    await act(async () => { expect(await result.current.actions.discard()).toBe(true); });
    expect(api.deleteDraft).toHaveBeenCalledWith("p-7");
  });
});

describe("verified facts", () => {
  test("loads once the product exists, and tells apart 'none on file' from 'loaded'", async () => {
    const api = makeApi({ getFacts: jest.fn(async () => ({ status: "Loaded", materials: [{ material: "cotton", percentage: 100 }], certifications: [], origin: "Portugal" })) });
    const { result } = setup(api);
    expect(result.current.facts.state).toBe("idle");

    await act(async () => { await result.current.actions.open("p-7"); });
    await flush();
    expect(api.getFacts).toHaveBeenCalledWith("p-7");
    expect(result.current.facts.state).toBe("loaded");
  });

  test("a request for facts creates the product if it needs to, and reports the outcome", async () => {
    const { api, result, type } = setup();
    type({ name: "Linen dress", text: "" });
    await act(async () => { expect(await result.current.actions.requestFacts("Composition please")).toBe(true); });

    expect(api.createProduct).toHaveBeenCalledWith("Linen dress");
    expect(api.requestFacts).toHaveBeenCalledWith("p-1", "Composition please");
    expect(result.current.factsRequest.state).toBe("sent");
  });

  test("asking before the product has a name says so", async () => {
    const { api, result } = setup();
    await act(async () => { expect(await result.current.actions.requestFacts("Composition please")).toBe(false); });
    expect(api.requestFacts).not.toHaveBeenCalled();
    expect(result.current.factsRequest).toMatchObject({ state: "error", message: "Give the product a name first, then ask for its facts." });
  });

  test("the server's reason is shown when the request is refused", async () => {
    const api = makeApi({ requestFacts: jest.fn(async () => { throw new ApiError(429, { detail: "You have already asked several times today." }); }) });
    const { result } = setup(api);
    await act(async () => { await result.current.actions.open("p-7"); });
    await act(async () => { await result.current.actions.requestFacts("Composition please"); });
    expect(result.current.factsRequest).toMatchObject({ state: "error", message: "You have already asked several times today." });
  });
});
