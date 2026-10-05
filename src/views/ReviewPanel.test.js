import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../services/api";
import ReviewPanel from "./ReviewPanel";

const item = (over = {}) => ({
  productId: "p-1",
  productName: "Wrap cardigan",
  finalDescription: "A soft wrap cardigan in a wool blend.",
  submittedAt: "2026-09-24T10:00:00Z",
  submittedByUserId: "auth0|writer",
  submittedByEmail: "ada@brand.com",
  market: "UK",
  aiStatus: "Ok",
  needsOverride: false,
  isOwnSubmission: false,
  canSignOff: true,
  ...over,
});

const makeApi = (over = {}) => ({
  getHistory: jest.fn(async () => ({
    versions: [{ text: "x", decisions: [
      { issueId: "i-1", phrase: "sustainable", critical: true, decision: "Kept", reason: "Certificate GRS-114 on file." },
      { issueId: "i-2", phrase: "eco-friendly", critical: false, decision: "Applied", reason: null },
    ] }],
  })),
  getFacts: jest.fn(async () => ({ status: "Loaded", materials: [{ material: "wool", percentage: 100 }], certifications: ["RWS"], origin: "Portugal" })),
  getFactsRequests: jest.fn(async () => [{ id: 1, productId: "p-1", note: "Please add the composition.", createdAt: "2026-09-23T10:00:00Z" }, { id: 2, productId: "other", note: "not this one", createdAt: "2026-09-23T10:00:00Z" }]),
  // re-checks the queue before deciding, matches by default so the stale check passes unless a test overrides it
  getPendingReviews: jest.fn(async () => [item()]),
  publish: jest.fn(async () => ({ success: true, overridden: false })),
  sendBack: jest.fn(async () => ({ status: "SentBack" })),
  ...over,
});

const setup = async (props = {}, api = makeApi()) => {
  const handlers = { onBack: jest.fn(), onDone: jest.fn(), onEditFacts: jest.fn() };
  render(<ReviewPanel item={item()} api={api} canAct canOverride canEditFacts {...handlers} {...props} />);
  await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });
  return { api, ...handlers };
};

beforeEach(() => jest.spyOn(window, "confirm").mockReturnValue(true));
afterEach(() => jest.restoreAllMocks());

describe("what the Senior Editor sees", () => {
  test("the copy, who submitted it, what the writer decided and the verified facts", async () => {
    await setup();

    expect(screen.getByText("A soft wrap cardigan in a wool blend.")).toBeInTheDocument();
    expect(screen.getByText(/ada@brand.com/)).toBeInTheDocument();
    expect(screen.getByText("Kept the wording")).toBeInTheDocument();
    expect(screen.getByText("Certificate GRS-114 on file.")).toBeInTheDocument();
    expect(screen.getByText("Applied the rewrite")).toBeInTheDocument();
    expect(screen.getByText(/100% wool/)).toBeInTheDocument();
    expect(screen.getByText("Portugal", { exact: false })).toBeInTheDocument();
  });

  test("only this product's requests for facts are shown", async () => {
    await setup();
    expect(screen.getByText(/Please add the composition/)).toBeInTheDocument();
    expect(screen.queryByText(/not this one/)).not.toBeInTheDocument();
  });

  test("when no facts are on file it says what that means", async () => {
    await setup({}, makeApi({ getFacts: jest.fn(async () => ({ status: "NoneOnFile", materials: [], certifications: [], origin: null })) }));
    expect(screen.getByText(/No verified facts on file/)).toBeInTheDocument();
  });
});

describe("publishing", () => {
  test("asks first, then publishes, and says what happened", async () => {
    const { api, onDone } = await setup();
    userEvent.click(screen.getByRole("button", { name: "Publish" }));

    await waitFor(() => expect(window.confirm).toHaveBeenCalledWith(expect.stringMatching(/Publish this product/)));
    await waitFor(() => expect(api.publish).toHaveBeenCalledWith("p-1", undefined));
    expect(onDone).toHaveBeenCalledWith("Wrap cardigan was published.");
  });

  test("does nothing if they change their mind", async () => {
    window.confirm.mockReturnValue(false);
    const { api } = await setup();
    userEvent.click(screen.getByRole("button", { name: "Publish" }));
    expect(api.publish).not.toHaveBeenCalled();
  });

  test("is disabled while something points to a block, with the reasons shown", async () => {
    await setup({ item: item({ needsOverride: true }) });

    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
    expect(screen.getByRole("alert")).toHaveTextContent(/kept a critical issue/);
  });

  test("is hidden on their own submission, with the reason, though they can still send it back", async () => {
    await setup({ item: item({ isOwnSubmission: true, canSignOff: false }) });

    expect(screen.queryByRole("button", { name: "Publish" })).not.toBeInTheDocument();
    expect(screen.getByText(/You submitted this yourself, so another Senior Editor has to sign it off/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send back" })).toBeInTheDocument();
  });

  test("when the server refuses, its reasons are shown and the override appears for someone who may use it", async () => {
    const api = makeApi({ publish: jest.fn(async () => { throw new ApiError(409, { message: "Publishing is blocked.", blockingReasons: ["Product facts could not be loaded."] }); }) });
    await setup({}, api);

    userEvent.click(screen.getByRole("button", { name: "Publish" }));
    expect(await screen.findByText("Product facts could not be loaded.")).toBeInTheDocument();
    expect(screen.getByLabelText(/Why should this be published anyway/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
  });

  test("someone without the override permission is told so, and gets no override form", async () => {
    const api = makeApi({ publish: jest.fn(async () => { throw new ApiError(409, { message: "Publishing is blocked.", blockingReasons: ["No AI check on this text."] }); }) });
    await setup({ canOverride: false }, api);

    userEvent.click(screen.getByRole("button", { name: "Publish" }));
    await screen.findByText("No AI check on this text.");
    expect(screen.queryByLabelText(/Why should this be published anyway/)).not.toBeInTheDocument();
    expect(screen.getByText(/can't override this/)).toBeInTheDocument();
  });
});

describe("overriding", () => {
  const blockedApi = () => makeApi({ publish: jest.fn(async (id, reason) => {
    if (reason) return { success: true, overridden: true };
    throw new ApiError(409, { message: "Publishing is blocked.", blockingReasons: ["A critical issue was kept."] });
  }) });

  test("needs a reason of at least 10 characters, asks first, and sends the reason", async () => {
    const { api, onDone } = await setup({ item: item({ needsOverride: true }) }, blockedApi());
    const override = screen.getByRole("button", { name: "Override and publish" });
    expect(override).toBeDisabled();

    userEvent.type(screen.getByLabelText(/Why should this be published anyway/), "too short");
    expect(override).toBeDisabled();
    userEvent.type(screen.getByLabelText(/Why should this be published anyway/), " and now long enough");
    expect(override).toBeEnabled();

    userEvent.click(override);
    await waitFor(() => expect(window.confirm).toHaveBeenCalledWith(expect.stringMatching(/recorded in the audit trail/)));
    await waitFor(() => expect(api.publish).toHaveBeenCalledWith("p-1", "too short and now long enough"));
    expect(onDone).toHaveBeenCalledWith("Wrap cardigan was published with your override.");
  });

  test("a refusal because the account lacks the permission is shown as the server said it", async () => {
    const api = makeApi({ publish: jest.fn(async () => { throw new ApiError(403, { message: "Your account is not allowed to override a block.", blockingReasons: ["A critical issue was kept."] }); }) });
    await setup({ item: item({ needsOverride: true }) }, api);

    userEvent.type(screen.getByLabelText(/Why should this be published anyway/), "Certificate checked today.");
    userEvent.click(screen.getByRole("button", { name: "Override and publish" }));

    expect(await screen.findByText("Your account is not allowed to override a block.")).toBeInTheDocument();
  });
});

describe("sending back", () => {
  test("needs a reason category and a comment of at least 10 characters", async () => {
    await setup();
    const send = screen.getByRole("button", { name: "Send back" });
    expect(send).toBeDisabled();

    userEvent.selectOptions(screen.getByLabelText(/which principle does it fall short of/i), "not_substantiated");
    expect(send).toBeDisabled();
    userEvent.type(screen.getByLabelText(/What needs to change/), "short");
    expect(send).toBeDisabled();
    userEvent.type(screen.getByLabelText(/What needs to change/), " and now more detail");
    expect(send).toBeEnabled();
  });

  test("asks first, then sends the category and comment, and says what happened", async () => {
    const { api, onDone } = await setup();
    userEvent.selectOptions(screen.getByLabelText(/which principle does it fall short of/i), "not_substantiated");
    userEvent.type(screen.getByLabelText(/What needs to change/), "The organic claim has no certificate.");
    userEvent.click(screen.getByRole("button", { name: "Send back" }));

    await waitFor(() => expect(window.confirm).toHaveBeenCalledWith(expect.stringMatching(/Send this back to its writer/)));
    await waitFor(() => expect(api.sendBack).toHaveBeenCalledWith("p-1", "not_substantiated", "The organic claim has no certificate."));
    expect(onDone).toHaveBeenCalledWith("Wrap cardigan was sent back to its writer.");
  });

  test("a refusal is shown in words", async () => {
    const api = makeApi({ sendBack: jest.fn(async () => { throw new ApiError(409, { detail: "Only a submission that is waiting for sign-off can be sent back." }); }) });
    await setup({}, api);
    userEvent.selectOptions(screen.getByLabelText(/which principle does it fall short of/i), "vague_or_unclear");
    userEvent.type(screen.getByLabelText(/What needs to change/), "Say which part is sustainable.");
    userEvent.click(screen.getByRole("button", { name: "Send back" }));

    expect(await screen.findByText("Only a submission that is waiting for sign-off can be sent back.")).toBeInTheDocument();
  });
});

describe("the facts", () => {
  test("Edit opens the facts for this product, for someone with the permission", async () => {
    const { onEditFacts } = await setup();
    userEvent.click(screen.getByRole("button", { name: "Edit facts" }));
    expect(onEditFacts).toHaveBeenCalledWith(expect.objectContaining({ productId: "p-1" }));
  });

  test("someone without the permission is told, and gets no edit button", async () => {
    await setup({ canEditFacts: false });
    expect(screen.queryByRole("button", { name: "Edit facts" })).not.toBeInTheDocument();
    expect(screen.getByText(/don't have permission to change the verified facts/)).toBeInTheDocument();
  });
});
